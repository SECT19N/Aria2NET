using System.Text.Json;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Xunit.Abstractions;

namespace Aria2NET.Tests;

/// <summary>
/// Phase 0 spike: runs the real aria2c against a loopback seeder and pins down the BitTorrent behaviour the 0.5 design relies on
/// (see "Torrents (0.5)" in docs/ARCHITECTURE.md). These double as regression tests for later aria2 upgrades.
/// </summary>
[Collection(RealAria2Collection.Name)]
public class TorrentSpikeTests(ITestOutputHelper output) {
	private static readonly (string, int)[] TwoFiles = [("a.bin", 40_000), ("sub/b.bin", 60_000)];


	private static async Task<string> AddTorrentAsync(Aria2RpcClient rpc, string base64, Dictionary<string, string> options) {
		using var result = await rpc.CallAsync("aria2.addTorrent", default, base64, Array.Empty<string>(), options);
		return result.RootElement.GetString()!;
	}

	private static async Task<JsonElement> FullStatusAsync(Aria2RpcClient rpc, string gid) {
		using var doc = await rpc.TellStatusFullAsync(gid);
		return doc.RootElement.Clone();
	}

	private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";

	[RequiresAria2Fact]
	public async Task Torrent_added_with_addTorrent_downloads_from_the_seeder_and_then_seeds() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await AddTorrentAsync(engine.Rpc!, swarm.Base64, new() { ["dir"] = swarm.LeechDir });
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.TotalBytes > 0 && i.CompletedBytes == i.TotalBytes);

		var status = await FullStatusAsync(engine.Rpc!, gid);
		output.WriteLine($"after completion: status={Str(status, "status")} seeder={Str(status, "seeder")} infoHash={Str(status, "infoHash")} name={status.GetProperty("bittorrent").GetProperty("info").GetProperty("name").GetString()}");

		Assert.Equal(100_000, item.TotalBytes);
		Assert.Equal(swarm.Torrent.InfoHash, Str(status, "infoHash"));
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
		Assert.Equal(swarm.Torrent.Files[1].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "sub", "b.bin")));
		// Default seed-ratio is 1.0, so a finished torrent stays "active" with seeder=true: this is what the Seeding status is derived from.
		Assert.Equal("active", Str(status, "status"));
		Assert.Equal("true", Str(status, "seeder"));

		// The app-facing model: the poll turns that into a Seeding torrent with its hash and name.
		var polled = (await engine.Rpc!.PollAsync()).Items.Single(i => i.Gid == gid);
		Assert.Equal(DownloadStatus.Seeding, polled.Status);
		Assert.True(polled.IsTorrent);
		Assert.Equal(swarm.Torrent.InfoHash, polled.InfoHash);
		Assert.Equal("swarm", polled.Name);
	}

	[RequiresAria2Fact]
	public async Task Paused_add_lets_the_file_selection_change_before_anything_downloads() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await AddTorrentAsync(engine.Rpc!, swarm.Base64, new() { ["dir"] = swarm.LeechDir, ["pause"] = "true", ["seed-time"] = "0" });
		var paused = await engine.Rpc!.TellStatusAsync(gid);
		Assert.Equal(DownloadStatus.Paused, paused.Status);
		Assert.Equal(0, paused.CompletedBytes);

		await engine.Rpc.ChangeOptionAsync(gid, new Dictionary<string, string> { ["select-file"] = "2" });
		await engine.Rpc.UnpauseAsync([gid]);
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		output.WriteLine($"selected file only: status={item.Status} total={item.TotalBytes} completed={item.CompletedBytes}");
		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[1].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "sub", "b.bin")));
		// Only the selected file (plus the pieces it shares with its neighbour) is fetched.
		Assert.InRange(item.TotalBytes, 60_000, 99_999);
	}

	[RequiresAria2Fact]
	public async Task Magnet_with_metadata_only_saves_a_torrent_file_and_finishes() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await engine.Rpc!.AddUriAsync([swarm.Torrent.Magnet(swarm.Tracker.AnnounceUrl)], new Dictionary<string, string> {
			["dir"] = swarm.LeechDir,
			["bt-metadata-only"] = "true",
			["bt-save-metadata"] = "true",
		});
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		var status = await FullStatusAsync(engine.Rpc, gid);

		var saved = Directory.EnumerateFiles(swarm.Root, swarm.Torrent.InfoHash + ".torrent", SearchOption.AllDirectories).FirstOrDefault() ?? "";
		output.WriteLine("saved metadata at: " + (saved.Length == 0 ? "(nowhere)" : saved));
		output.WriteLine($"metadata-only: status={item.Status} followedBy={(status.TryGetProperty("followedBy", out var f) ? f.ToString() : "-")} savedExists={saved.Length > 0}");
		foreach (var file in Directory.EnumerateFiles(swarm.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("logs") && !f.Contains("backups"))) {
			output.WriteLine("  file: " + Path.GetRelativePath(swarm.Root, file));
		}

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.True(saved.Length > 0, "bt-save-metadata should write <infohash>.torrent");
		Assert.False(status.TryGetProperty("followedBy", out var spawned), $"metadata-only must not spawn a follower download (got {spawned})");
	}

	[RequiresAria2Fact]
	public async Task Magnet_with_pause_metadata_chains_to_a_paused_follower() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await engine.Rpc!.AddUriAsync([swarm.Torrent.Magnet(swarm.Tracker.AnnounceUrl)], new Dictionary<string, string> {
			["dir"] = swarm.LeechDir,
			["pause-metadata"] = "true",
			["bt-save-metadata"] = "true",
		});
		var metadata = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		var status = await FullStatusAsync(engine.Rpc, gid);
		var follower = status.TryGetProperty("followedBy", out var fb) && fb.GetArrayLength() > 0 ? fb[0].GetString() : null;
		output.WriteLine($"pause-metadata: metadata status={metadata.Status} follower={follower ?? "-"}");
		foreach (var file in Directory.EnumerateFiles(swarm.Root, "*", SearchOption.AllDirectories)) {
			output.WriteLine("  file after metadata: " + Path.GetRelativePath(swarm.Root, file));
		}

		Assert.NotNull(follower);
		var next = await engine.Rpc.TellStatusAsync(follower!);
		var nextFull = await FullStatusAsync(engine.Rpc, follower!);
		output.WriteLine($"follower: status={next.Status} belongsTo={Str(nextFull, "belongsTo")} files={nextFull.GetProperty("files").GetArrayLength()} completed={next.CompletedBytes}");
		output.WriteLine($"belongsTo on follower: '{Str(nextFull, "belongsTo")}' (aria2 only links forward through followedBy)");
		Assert.Equal(DownloadStatus.Paused, next.Status);

		// The whole point: pick files now, then start.
		await engine.Rpc.ChangeOptionAsync(follower!, new Dictionary<string, string> { ["select-file"] = "1", ["seed-time"] = "0" });
		await engine.Rpc.UnpauseAsync([follower!]);
		var done = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, follower!, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, done.Status);
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
	}

	[RequiresAria2Fact]
	public async Task Seed_ratio_zero_ends_the_download_as_complete_instead_of_seeding() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await AddTorrentAsync(engine.Rpc!, swarm.Base64, new() { ["dir"] = swarm.LeechDir, ["seed-time"] = "0" });
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
	}

	[RequiresAria2Fact]
	public async Task Check_integrity_accepts_existing_data_without_downloading_it() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		// The data is already on disk (as if the user copied it over): the seeder's payload folder is also the leecher's target.
		var existing = swarm.Torrent.WritePayload(swarm.LeechDir);

		var gid = await AddTorrentAsync(engine.Rpc!, swarm.Base64, new() { ["dir"] = existing, ["check-integrity"] = "true", ["seed-time"] = "0" });
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		var status = await FullStatusAsync(engine.Rpc!, gid);
		output.WriteLine($"recheck: status={item.Status} downloadSpeed={Str(status, "downloadSpeed")} completed={item.CompletedBytes}");

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(100_000, item.CompletedBytes);
	}

	[RequiresAria2Fact]
	public async Task Magnet_without_peers_reports_a_metadata_placeholder() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var torrent = TestTorrent.Create("lonely", "http://127.0.0.1:1/announce", ("x.bin", 20_000));

		var gid = await engine.Rpc!.AddUriAsync([torrent.Magnet("http://127.0.0.1:1/announce")], new Dictionary<string, string> { ["dir"] = Path.Combine(temp.Path, "dl") });
		await Task.Delay(1500);
		var status = await FullStatusAsync(engine.Rpc, gid);
		output.WriteLine(status.GetRawText());
		var item = await engine.Rpc.TellStatusAsync(gid);
		output.WriteLine($"item: name={item.Name} status={item.Status} path={item.Path} uri={item.FirstUri}");

		Assert.Equal("active", Str(status, "status"));
		Assert.Equal(torrent.InfoHash, Str(status, "infoHash"));
	}

	[RequiresAria2Fact]
	public async Task An_unfinished_torrent_comes_back_after_an_engine_restart_with_the_same_gid_and_hash() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var gid = await AddTorrentAsync(engine.Rpc!, swarm.Base64, new() { ["dir"] = swarm.LeechDir, ["pause"] = "true" });

		await engine.RestartAsync();

		var item = (await engine.Rpc!.PollAsync()).Items.Single(i => i.Gid == gid);
		Assert.Equal(swarm.Torrent.InfoHash, item.InfoHash);
		Assert.Equal(DownloadStatus.Paused, item.Status);
		Assert.True(item.IsTorrent);
	}

	[RequiresAria2Fact]
	public async Task A_seeding_torrent_is_not_kept_by_aria2_across_a_restart() {
		using var swarm = new Swarm(TwoFiles);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var gid = await AddTorrentAsync(engine.Rpc!, swarm.Base64, new() { ["dir"] = swarm.LeechDir });
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status == DownloadStatus.Seeding);

		await engine.RestartAsync();

		// aria2 saves unfinished downloads only; data that is complete is not "unfinished" even while uploading.
		Assert.DoesNotContain(gid, (await engine.Rpc!.PollAsync()).Items.Select(i => i.Gid));
		Assert.Equal(100_000, new FileInfo(Path.Combine(swarm.LeechDir, "swarm", "sub", "b.bin")).Length + new FileInfo(Path.Combine(swarm.LeechDir, "swarm", "a.bin")).Length);
	}
}
