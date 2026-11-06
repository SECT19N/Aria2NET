using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;

namespace Aria2NET.Tests;

public class TorrentHistoryTests {
	private const string Hash = "2b23b147743538b694a5049fc9f9ba676000e7d4";

	private static (HistoryStore Store, Database Db) Create(TempDir temp) {
		var db = new Database(Path.Combine(temp.Path, "history.db"), Path.Combine(temp.Path, "backups"));
		db.EnsureReady();
		return (new HistoryStore(db), db);
	}

	private static DownloadItem Torrent(string gid, DownloadStatus status = DownloadStatus.Active, string hash = Hash, long uploaded = 0, bool metadata = false) =>
		new(gid, "My Album", status, 1000, 400, 0, null, @"C:\dl", null, InfoHash: hash, IsTorrent: true, UploadedBytes: uploaded, IsMetadata: metadata);

	[Fact]
	public void Schema_v2_adds_the_torrent_columns_and_keeps_old_rows() {
		using var temp = new TempDir();
		var path = Path.Combine(temp.Path, "history.db");
		var backups = Path.Combine(temp.Path, "backups");
		var v1Only = Migrations.All.Take(1).ToList();
		var old = new Database(path, backups, migrations: v1Only);
		old.EnsureReady();
		using (var c = old.Open()) {
			// The store now writes the v2 columns, so a row from the old app is inserted by hand.
			using var insert = c.CreateCommand();
			insert.CommandText = "INSERT INTO downloads (gid, name, status, added_utc, updated_utc) VALUES ('old', 'a.zip', 'complete', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z')";
			insert.ExecuteNonQuery();
		}

		var upgraded = new Database(path, backups);
		Assert.Equal(DatabaseStatus.Ready, upgraded.EnsureReady().Status);

		var entry = new HistoryStore(upgraded).Get("old")!;
		Assert.Equal("a.zip", entry.Name);
		Assert.Null(entry.InfoHash);
		Assert.False(entry.IsTorrent);
		Assert.Equal(0, entry.UploadedBytes);
		Assert.Single(Directory.GetFiles(backups, "history-v1-*.db"));
	}

	[Fact]
	public void Upsert_stores_the_hash_and_looks_it_up_case_insensitively() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);

		store.Upsert([Torrent("t1", uploaded: 123)], DateTime.UtcNow);

		var found = store.FindByInfoHash(Hash.ToUpperInvariant())!;
		Assert.Equal("t1", found.Gid);
		Assert.True(found.IsTorrent);
		Assert.Equal(123, found.UploadedBytes);
		Assert.Null(store.FindByInfoHash("0000000000000000000000000000000000000000"));
	}

	[Fact]
	public void Update_never_loses_the_hash_or_lowers_the_uploaded_total() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);
		store.Upsert([Torrent("t1", uploaded: 500)], DateTime.UtcNow);

		// After a restart aria2 may report no hash for a moment and an upload counter that started over.
		store.Upsert([Torrent("t1", hash: "", uploaded: 10) with { InfoHash = null, IsTorrent = false }], DateTime.UtcNow);

		var entry = store.Get("t1")!;
		Assert.Equal(Hash, entry.InfoHash);
		Assert.True(entry.IsTorrent);
		Assert.Equal(500, entry.UploadedBytes);
	}

	[Fact]
	public void FindByInfoHash_returns_the_newest_row() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);
		store.Upsert([Torrent("old")], DateTime.UtcNow.AddHours(-2));
		store.Upsert([Torrent("new")], DateTime.UtcNow);

		Assert.Equal("new", store.FindByInfoHash(Hash)!.Gid);
	}

	[Fact]
	public void Reconcile_relinks_a_torrent_whose_gid_changed_by_its_info_hash() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);
		store.Upsert([Torrent("before")], DateTime.UtcNow.AddMinutes(-5));

		// No URL and a different folder would defeat the URL match: only the hash identifies it.
		var result = store.Reconcile([Torrent("after") with { Dir = @"D:\elsewhere" }], DateTime.UtcNow);

		Assert.Equal(new ReconcileResult(Interrupted: 0, Relinked: 1), result);
		Assert.Null(store.Get("before"));
		Assert.Equal(DownloadStatus.Active.ToString().ToLowerInvariant(), store.Get("after")!.Status);
		Assert.Equal(1, store.Count());
	}

	[Fact]
	public void Reconcile_marks_a_torrent_aria2_no_longer_knows_as_interrupted() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);
		store.Upsert([Torrent("gone")], DateTime.UtcNow.AddMinutes(-5));

		var result = store.Reconcile([], DateTime.UtcNow);

		Assert.Equal(1, result.Interrupted);
		Assert.Equal(HistoryStatus.Interrupted, store.Get("gone")!.Status);
	}

	[Fact]
	public void Seeding_is_recorded_as_complete() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);

		store.Upsert([Torrent("s", DownloadStatus.Seeding)], DateTime.UtcNow);

		Assert.Equal(HistoryStatus.Complete, store.Get("s")!.Status);
		Assert.NotNull(store.Get("s")!.FinishedUtc);
	}

	[Fact]
	public void Recorder_ignores_metadata_placeholders_and_records_the_follower() {
		using var temp = new TempDir();
		var (store, _) = Create(temp);
		var recorder = new HistoryRecorder(store);
		recorder.OnSnapshot(new PollSnapshot([], GlobalStats.Empty)); // the reconcile pass

		recorder.OnSnapshot(new PollSnapshot([Torrent("meta", metadata: true)], GlobalStats.Empty));
		Assert.Equal(0, store.Count());

		recorder.OnSnapshot(new PollSnapshot([Torrent("meta", DownloadStatus.Complete, metadata: true), Torrent("follower", DownloadStatus.Paused)], GlobalStats.Empty));
		Assert.Equal(1, store.Count());
		Assert.Equal("follower", store.FindByInfoHash(Hash)!.Gid);
	}

	[Fact]
	public void Finished_metadata_placeholder_is_superseded_but_a_failed_one_is_still_shown() {
		Assert.True(DownloadView.IsSuperseded(Torrent("m", DownloadStatus.Complete, metadata: true)));
		Assert.False(DownloadView.IsSuperseded(Torrent("m", DownloadStatus.Active, metadata: true)));
		Assert.False(DownloadView.IsSuperseded(Torrent("m", DownloadStatus.Error, metadata: true)));
		Assert.False(DownloadView.IsSuperseded(Torrent("t", DownloadStatus.Complete)));
	}

	[Fact]
	public void Index_on_the_hash_exists() {
		using var temp = new TempDir();
		var (_, db) = Create(temp);
		using var c = db.Open();
		using var cmd = c.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_downloads_hash'";

		Assert.Equal(1L, cmd.ExecuteScalar());
	}
}
