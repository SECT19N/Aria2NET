using System.Text.Json;
using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Microsoft.Data.Sqlite;

namespace Aria2NET.Tests;

public class DatabaseTests {
	private static Database Create(TempDir temp, IReadOnlyList<Migration>? migrations = null) =>
		new(Path.Combine(temp.Path, "history.db"), Path.Combine(temp.Path, "backups"), migrations: migrations);

	private static long UserVersion(Database db) {
		using var c = db.Open();
		using var cmd = c.CreateCommand();
		cmd.CommandText = "PRAGMA user_version";
		return (long) cmd.ExecuteScalar()!;
	}

	[Fact]
	public void A_new_database_is_created_at_the_newest_version_with_a_start_backup() {
		using var temp = new TempDir();
		var db = Create(temp);

		var result = db.EnsureReady();

		Assert.Equal(DatabaseStatus.Ready, result.Status);
		Assert.Equal(Migrations.Latest, UserVersion(db));
		Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "backups"), "history-start-*.db"));
	}

	[Fact]
	public void Every_start_adds_a_backup_and_only_the_newest_few_are_kept() {
		using var temp = new TempDir();
		var db = new Database(Path.Combine(temp.Path, "history.db"), Path.Combine(temp.Path, "backups"), startBackups: 3);
		for (var i = 0; i < 6; i++) {
			db.EnsureReady();
		}
		Assert.Equal(3, Directory.GetFiles(Path.Combine(temp.Path, "backups"), "history-start-*.db").Length);
	}

	[Fact]
	public void Upgrading_keeps_the_data_and_backs_up_the_old_file_first() {
		using var temp = new TempDir();
		var v1 = Migrations.All;
		Create(temp, v1).EnsureReady();
		var oldDb = Create(temp, v1);
		new HistoryStore(oldDb).Upsert([new DownloadItem("g1", "a.zip", DownloadStatus.Complete, 10, 10, 0, null, @"C:\d", "http://h/a.zip")], DateTime.UtcNow);

		var v2 = v1.Append(new Migration(v1[^1].Version + 1, "ALTER TABLE downloads ADD COLUMN note TEXT;")).ToList();
		var upgraded = Create(temp, v2);
		var result = upgraded.EnsureReady();

		Assert.Equal(DatabaseStatus.Ready, result.Status);
		Assert.Equal(v2[^1].Version, UserVersion(upgraded));
		var backup = Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "backups"), $"history-v{v1[^1].Version}-*.db"));
		// The backup is the schema before the upgrade, with the row in it; the live file has the new column and the row.
		using (var c = new SqliteConnection($"Data Source={backup};Pooling=False")) {
			c.Open();
			using var cmd = c.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM downloads";
			Assert.Equal(1L, cmd.ExecuteScalar());
		}
		using var live = upgraded.Open();
		using var count = live.CreateCommand();
		count.CommandText = "SELECT COUNT(*), COUNT(note) FROM downloads";
		using var reader = count.ExecuteReader();
		Assert.True(reader.Read());
		Assert.Equal(1L, reader.GetInt64(0));
	}

	[Fact]
	public void A_database_from_a_newer_app_is_left_untouched() {
		using var temp = new TempDir();
		var v1 = Migrations.All;
		var v2 = v1.Append(new Migration(v1[^1].Version + 1, "ALTER TABLE downloads ADD COLUMN note TEXT;")).ToList();
		Create(temp, v2).EnsureReady();

		var older = Create(temp, v1);
		var result = older.EnsureReady();

		Assert.Equal(DatabaseStatus.TooNew, result.Status);
		Assert.Equal(v2[^1].Version, UserVersion(older));
	}

	[Fact]
	public void A_corrupt_file_is_moved_aside_and_replaced() {
		using var temp = new TempDir();
		var file = Path.Combine(temp.Path, "history.db");
		File.WriteAllText(file, new string('x', 4096));
		var db = Create(temp);

		var result = db.EnsureReady();

		Assert.Equal(DatabaseStatus.Recovered, result.Status);
		Assert.NotNull(result.MovedTo);
		Assert.True(File.Exists(result.MovedTo));
		Assert.Equal(Migrations.Latest, UserVersion(db));
		Assert.Equal(0, new HistoryStore(db).Count());
	}
}

public class DataFolderLockTests {
	[Fact]
	public void A_second_holder_is_refused_until_the_first_lets_go() {
		using var temp = new TempDir();
		var file = Path.Combine(temp.Path, "aria2net.lock");

		var first = DataFolderLock.TryAcquire(file);
		Assert.NotNull(first);
		Assert.Null(DataFolderLock.TryAcquire(file));

		first.Dispose();
		using var again = DataFolderLock.TryAcquire(file);
		Assert.NotNull(again);
	}
}

public class BackupRotationTests {
	[Fact]
	public void Prune_keeps_the_newest_files_of_one_prefix_only() {
		using var temp = new TempDir();
		var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
		for (var i = 0; i < 5; i++) {
			File.WriteAllText(BackupRotation.NewPath(temp.Path, "a", ".bak", start.AddMinutes(i)), "x");
		}
		File.WriteAllText(BackupRotation.NewPath(temp.Path, "b", ".bak", start), "x");

		BackupRotation.Prune(temp.Path, "a", keep: 2);

		var kept = Directory.GetFiles(temp.Path, "a-*").Select(Path.GetFileName).Order().ToList();
		Assert.Equal(["a-20261101000300000.bak", "a-20261101000400000.bak"], kept);
		Assert.Single(Directory.GetFiles(temp.Path, "b-*"));
	}

	[Fact]
	public void Two_backups_in_the_same_millisecond_do_not_overwrite_each_other() {
		using var temp = new TempDir();
		var now = DateTime.UtcNow;
		var first = BackupRotation.NewPath(temp.Path, "a", ".bak", now);
		File.WriteAllText(first, "1");
		var second = BackupRotation.NewPath(temp.Path, "a", ".bak", now);
		Assert.NotEqual(first, second);
	}
}

public class FileCategoryTests {
	[Theory]
	[InlineData("movie.MKV", "Video")]
	[InlineData("song.flac", "Audio")]
	[InlineData("pic.jpeg", "Image")]
	[InlineData("a.tar.gz", "Archive")]
	[InlineData("setup.exe", "Program")]
	[InlineData("report.pdf", "Document")]
	[InlineData("x.torrent", "Torrent")]
	[InlineData("noextension", "Other")]
	[InlineData("weird.zzz", "Other")]
	public void Category_comes_from_the_extension(string name, string expected) => Assert.Equal(expected, FileCategory.FromName(name));
}

public class HistoryStoreTests {
	private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

	private static HistoryStore NewStore(TempDir temp) {
		var db = new Database(Path.Combine(temp.Path, "history.db"), Path.Combine(temp.Path, "backups"));
		db.EnsureReady();
		return new HistoryStore(db);
	}

	internal static DownloadItem Item(string gid, DownloadStatus status = DownloadStatus.Active, string name = "file.zip", string? uri = "http://example.com/file.zip",
		long total = 100, string dir = @"C:\dl", int? code = null, string? message = null) =>
		new(gid, name, status, total, status == DownloadStatus.Complete ? total : 10, 0, message, dir, uri, Path.Combine(dir, name), code);

	[Fact]
	public void Upsert_inserts_once_keeps_the_added_time_and_sets_the_finished_time_when_it_ends() {
		using var temp = new TempDir();
		var store = NewStore(temp);

		store.Upsert([Item("g1")], T0);
		store.Upsert([Item("g1", DownloadStatus.Complete)], T0.AddMinutes(5));
		store.Upsert([Item("g1", DownloadStatus.Complete)], T0.AddMinutes(9));

		var row = Assert.Single(store.Query(new HistoryQuery()));
		Assert.Equal(HistoryStatus.Complete, row.Status);
		Assert.Equal(T0, row.AddedUtc);
		Assert.Equal(T0.AddMinutes(5), row.FinishedUtc);
		Assert.Equal("example.com", row.Host);
		Assert.Equal("Archive", row.Category);
		Assert.Equal(100, row.TotalBytes);
	}

	[Fact]
	public void Errors_keep_their_code_and_message() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([Item("g1", DownloadStatus.Error, code: 3, message: "Resource not found")], T0);

		var row = store.Get("g1")!;
		Assert.Equal(3, row.ErrorCode);
		Assert.Equal("Resource not found", row.ErrorMessage);
		Assert.NotNull(row.FinishedUtc);
	}

	[Fact]
	public void Search_matches_name_url_and_path_and_treats_wildcards_literally() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([
			Item("g1", name: "holiday_photos.zip", uri: "http://a.com/x"),
			Item("g2", name: "report 100% final.pdf", uri: "http://b.com/y"),
			Item("g3", name: "other.bin", uri: "http://c.com/needle.bin"),
		], T0);

		Assert.Equal(["g1"], store.Query(new HistoryQuery(Search: "holiday")).Select(e => e.Gid));
		Assert.Equal(["g2"], store.Query(new HistoryQuery(Search: "100%")).Select(e => e.Gid));
		Assert.Equal(["g3"], store.Query(new HistoryQuery(Search: "needle")).Select(e => e.Gid));
		Assert.Empty(store.Query(new HistoryQuery(Search: "holiday_phot_s")));
		Assert.Equal(3, store.Count(new HistoryQuery(Search: "  ")));
	}

	[Fact]
	public void Filters_combine_and_sorting_and_paging_work() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([Item("g1", DownloadStatus.Complete, "b.mp4", "http://one.com/b.mp4", total: 300)], T0);
		store.Upsert([Item("g2", DownloadStatus.Error, "a.mp4", "http://two.com/a.mp4", total: 100)], T0.AddDays(1));
		store.Upsert([Item("g3", DownloadStatus.Complete, "c.zip", "http://one.com/c.zip", total: 200)], T0.AddDays(2));

		Assert.Equal(["g3", "g2", "g1"], store.Query(new HistoryQuery()).Select(e => e.Gid));
		Assert.Equal(["g1", "g2", "g3"], store.Query(new HistoryQuery(Descending: false)).Select(e => e.Gid));
		Assert.Equal(["g1", "g3", "g2"], store.Query(new HistoryQuery(Sort: HistorySort.Size)).Select(e => e.Gid));
		Assert.Equal(["g3", "g1"], store.Query(new HistoryQuery(Statuses: [HistoryStatus.Complete])).Select(e => e.Gid));
		Assert.Equal(["g3", "g1"], store.Query(new HistoryQuery(Host: "ONE.com")).Select(e => e.Gid));
		Assert.Equal(["g2", "g1"], store.Query(new HistoryQuery(Category: "Video")).Select(e => e.Gid));
		Assert.Equal(["g2"], store.Query(new HistoryQuery(FromUtc: T0.AddHours(1), ToUtc: T0.AddDays(2))).Select(e => e.Gid));
		Assert.Equal(["g2"], store.Query(new HistoryQuery(Limit: 1, Offset: 1)).Select(e => e.Gid));
		Assert.Equal(["one.com", "two.com"], store.Hosts());
		Assert.Equal(2, store.Count(new HistoryQuery(Host: "one.com")));
	}

	[Fact]
	public void Name_sort_ignores_case() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([Item("g1", name: "banana.zip"), Item("g2", name: "Apple.zip"), Item("g3", name: "cherry.zip")], T0);
		Assert.Equal(["g2", "g1", "g3"], store.Query(new HistoryQuery(Sort: HistorySort.Name, Descending: false)).Select(e => e.Gid));
	}

	[Fact]
	public void Delete_removes_only_the_chosen_rows() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([Item("g1"), Item("g2"), Item("g3")], T0);
		var ids = store.Query(new HistoryQuery()).Where(e => e.Gid != "g2").Select(e => e.Id).ToList();

		Assert.Equal(2, store.Delete(ids));
		Assert.Equal(["g2"], store.Query(new HistoryQuery()).Select(e => e.Gid));
	}

	[Fact]
	public void Clear_and_Purge_leave_unfinished_downloads_alone() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([Item("old", DownloadStatus.Complete)], T0);
		store.Upsert([Item("recent", DownloadStatus.Complete)], T0.AddDays(40));
		store.Upsert([Item("live", DownloadStatus.Paused)], T0);

		Assert.Equal(1, store.Purge(T0.AddDays(30)));
		Assert.Equal(["live", "recent"], store.Query(new HistoryQuery(Sort: HistorySort.Name, Descending: false)).Select(e => e.Gid).Order().ToList());

		Assert.Equal(1, store.Clear());
		Assert.Equal(["live"], store.Query(new HistoryQuery()).Select(e => e.Gid));
	}

	[Fact]
	public void Reconcile_marks_vanished_downloads_interrupted_and_relinks_a_changed_gid() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([
			Item("same", DownloadStatus.Paused, "a.zip", "http://h/a.zip"),
			Item("lost", DownloadStatus.Active, "b.zip", "http://h/b.zip"),
			Item("moved", DownloadStatus.Waiting, "c.zip", "http://h/c.zip"),
			Item("done", DownloadStatus.Complete, "d.zip", "http://h/d.zip"),
		], T0);

		var result = store.Reconcile([
			Item("same", DownloadStatus.Paused, "a.zip", "http://h/a.zip"),
			Item("moved2", DownloadStatus.Waiting, "c.zip", "http://h/c.zip"),
			Item("brandnew", DownloadStatus.Active, "e.zip", "http://h/e.zip"),
		], T0.AddHours(1));

		Assert.Equal(new ReconcileResult(Interrupted: 1, Relinked: 1), result);
		Assert.Equal(HistoryStatus.Interrupted, store.Get("lost")!.Status);
		Assert.Null(store.Get("moved"));
		var relinked = store.Get("moved2")!;
		Assert.Equal(T0, relinked.AddedUtc);
		Assert.Equal(HistoryStatus.Complete, store.Get("done")!.Status);
		Assert.Equal(HistoryStatus.Active, store.Get("brandnew")!.Status);
		Assert.Equal(5, store.Count());
	}

	[Fact]
	public void MarkRemoved_only_touches_unfinished_rows() {
		using var temp = new TempDir();
		var store = NewStore(temp);
		store.Upsert([Item("a", DownloadStatus.Active), Item("b", DownloadStatus.Complete)], T0);

		store.MarkRemoved(["a", "b"], T0.AddMinutes(1));

		Assert.Equal(HistoryStatus.Removed, store.Get("a")!.Status);
		Assert.Equal(T0.AddMinutes(1), store.Get("a")!.FinishedUtc);
		Assert.Equal(HistoryStatus.Complete, store.Get("b")!.Status);
	}
}

public class HistoryRecorderTests {
	private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

	private static (HistoryRecorder Recorder, HistoryStore Store, Func<DateTime> Now, Action<TimeSpan> Advance) Create(TempDir temp) {
		var db = new Database(Path.Combine(temp.Path, "history.db"), Path.Combine(temp.Path, "backups"));
		db.EnsureReady();
		var store = new HistoryStore(db);
		var now = T0;
		return (new HistoryRecorder(store, clock: () => now), store, () => now, d => now += d);
	}

	private static PollSnapshot Snap(params DownloadItem[] items) => new(items, GlobalStats.Empty);

	[Fact]
	public void A_snapshot_is_written_only_when_something_changed() {
		using var temp = new TempDir();
		var (recorder, store, _, advance) = Create(temp);

		recorder.OnSnapshot(Snap());                                                                // reconcile on the first snapshot
		recorder.OnSnapshot(Snap(HistoryStoreTests.Item("g1")));
		var firstWrite = store.Get("g1")!;
		advance(TimeSpan.FromMinutes(1));
		recorder.OnSnapshot(Snap(HistoryStoreTests.Item("g1")));                                    // unchanged: no write
		Assert.Equal(firstWrite, store.Get("g1"));

		advance(TimeSpan.FromMinutes(1));
		recorder.OnSnapshot(Snap(HistoryStoreTests.Item("g1", DownloadStatus.Complete)));
		var done = store.Get("g1")!;
		Assert.Equal(HistoryStatus.Complete, done.Status);
		Assert.Equal(T0.AddMinutes(2), done.FinishedUtc);
	}

	[Fact]
	public void A_download_that_vanishes_while_unfinished_is_recorded_as_removed_but_a_finished_one_is_not_touched() {
		using var temp = new TempDir();
		var (recorder, store, _, advance) = Create(temp);
		recorder.OnSnapshot(Snap());
		recorder.OnSnapshot(Snap(HistoryStoreTests.Item("busy"), HistoryStoreTests.Item("done", DownloadStatus.Complete)));
		advance(TimeSpan.FromMinutes(1));

		recorder.OnSnapshot(Snap());

		Assert.Equal(HistoryStatus.Removed, store.Get("busy")!.Status);
		Assert.Equal(HistoryStatus.Complete, store.Get("done")!.Status);
	}

	[Fact]
	public void After_an_engine_restart_the_first_snapshot_is_reconciled_instead_of_treated_as_removals() {
		using var temp = new TempDir();
		var (recorder, store, _, _) = Create(temp);
		recorder.OnSnapshot(Snap());
		recorder.OnSnapshot(Snap(HistoryStoreTests.Item("kept", DownloadStatus.Paused), HistoryStoreTests.Item("lost")));

		recorder.EngineStarted();
		recorder.OnSnapshot(Snap(HistoryStoreTests.Item("kept", DownloadStatus.Paused)));

		Assert.Equal(HistoryStatus.Paused, store.Get("kept")!.Status);
		Assert.Equal(HistoryStatus.Interrupted, store.Get("lost")!.Status);
	}

	[Fact]
	public void A_database_failure_is_swallowed() {
		using var temp = new TempDir();
		var (recorder, _, _, _) = Create(temp);
		File.Delete(Path.Combine(temp.Path, "history.db"));
		Directory.Delete(Path.Combine(temp.Path, "backups"), true);
		// Replace the database file with a directory so every open fails.
		Directory.CreateDirectory(Path.Combine(temp.Path, "history.db"));

		var ex = Record.Exception(() => recorder.OnSnapshot(Snap(HistoryStoreTests.Item("g1"))));

		Assert.Null(ex);
	}
}

public class HistoryExportTests {
	private static HistoryEntry Entry(string name, string? error = null, string? uri = "http://h/x") =>
		new(1, "g", uri, @"C:\dl", @"C:\dl\" + name, name, "h", "Other", 1234, 1234, "complete", null, error,
			new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 12, 5, 0, DateTimeKind.Utc));

	[Fact]
	public void Csv_has_a_header_quotes_special_characters_and_defuses_formulas() {
		var csv = HistoryExport.ToCsv([Entry("plain.zip"), Entry("a,b \"c\".zip"), Entry("=cmd|calc.zip", error: "line1\nline2")]);
		var lines = csv.Split("\r\n");

		Assert.StartsWith("Name,Status,Size (bytes)", lines[0]);
		Assert.StartsWith("plain.zip,complete,1234,http://h/x,", lines[1]);
		Assert.Contains("\"a,b \"\"c\"\".zip\"", lines[2]);
		Assert.Contains("'=cmd|calc.zip", csv);
		Assert.Contains("\"line1\nline2\"", csv);
		Assert.Contains("2026-11-01T12:00:00Z", lines[1]);
	}

	[Fact]
	public void Json_is_valid_and_keeps_nulls() {
		var json = HistoryExport.ToJson([Entry("a.zip", uri: null)]);
		using var doc = JsonDocument.Parse(json);
		var first = doc.RootElement[0];

		Assert.Equal("a.zip", first.GetProperty("name").GetString());
		Assert.Equal(1234, first.GetProperty("sizeBytes").GetInt64());
		Assert.Equal(JsonValueKind.Null, first.GetProperty("url").ValueKind);
		Assert.Equal("2026-11-01T12:05:00Z", first.GetProperty("finishedUtc").GetString());
	}
}

public class HistoryLookupTests {
	[Fact]
	public void Folders_are_listed_newest_first_and_a_file_is_found_by_path_ignoring_case() {
		using var temp = new TempDir();
		var db = new Database(Path.Combine(temp.Path, "history.db"), Path.Combine(temp.Path, "backups"));
		db.EnsureReady();
		var store = new HistoryStore(db);
		var t = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
		store.Upsert([HistoryStoreTests.Item("g1", dir: @"C:\old", name: "a.zip")], t);
		store.Upsert([HistoryStoreTests.Item("g2", dir: @"C:\new", name: "b.zip")], t.AddDays(1));
		store.Upsert([HistoryStoreTests.Item("g3", dir: @"C:\old", name: "c.zip")], t.AddDays(2));

		Assert.Equal([@"C:\old", @"C:\new"], store.Folders());
		Assert.Equal("g2", store.FindByPath(@"c:\NEW\B.ZIP")!.Gid);
		Assert.Null(store.FindByPath(@"C:\nowhere\x.zip"));
	}
}
