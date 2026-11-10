using System.Net;
using System.Text;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class TorrentConfigTests {
	private static readonly AppPaths Paths = new(@"C:\data", @"C:\tools");

	[Fact]
	public void Render_enables_bittorrent_features_and_keeps_dht_state_in_the_engine_folder() {
		var text = new Aria2Config(6800, "abc", 42, @"C:\dl").Render(Paths);

		Assert.Contains("bt-save-metadata=true", text);
		Assert.Contains("bt-load-saved-metadata=true", text);
		Assert.Contains("enable-dht=true", text);
		Assert.Contains("bt-enable-lpd=true", text);
		Assert.Contains("seed-ratio=1.0", text);
		Assert.Contains("file-allocation=none", text);
		Assert.Contains(@"dht-file-path=C:\data\engine\dht.dat", text);
		Assert.Contains(@"dht-file-path6=C:\data\engine\dht6.dat", text);
		Assert.DoesNotContain("bt-tracker", text);
	}

	[Fact]
	public void Render_adds_the_tracker_list_as_one_comma_separated_option() {
		var config = new Aria2Config(6800, "abc", 42, @"C:\dl", ["udp://a:1/announce", "https://b/announce"]);

		Assert.Contains("bt-tracker=udp://a:1/announce,https://b/announce", config.Render(Paths));
	}

	[Fact]
	public void Parse_keeps_http_https_and_udp_trackers_only() {
		var list = TrackerListService.Parse("# best trackers\r\nudp://tracker.example:6969/announce\r\n\r\nhttps://t.example/announce\r\nftp://nope/x\r\nhttp://t.example:80/announce\r\nnot a url\r\nwss://t/x\r\n");

		Assert.Equal(["udp://tracker.example:6969/announce", "https://t.example/announce", "http://t.example:80/announce"], list);
	}

	[Fact]
	public void Parse_drops_duplicates_regardless_of_case() {
		Assert.Single(TrackerListService.Parse("udp://A.example:1/announce\nudp://a.example:1/announce\n"));
	}

	[Theory]
	[InlineData("udp://a:1/announce,udp://b:1/announce")]
	[InlineData("udp://a:1/an nounce")]
	[InlineData("rpc-secret=evil")]
	[InlineData("udp://a:1/\tx")]
	public void Parse_rejects_lines_that_could_smuggle_a_config_option(string line) {
		Assert.Empty(TrackerListService.Parse(line));
	}

	[Fact]
	public void Parse_caps_the_list() {
		var text = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"udp://t{i}.example:1/announce"));

		Assert.Equal(TrackerListService.MaxTrackers, TrackerListService.Parse(text).Count);
	}

	[Fact]
	public void Parse_handles_null_and_empty() {
		Assert.Empty(TrackerListService.Parse(null));
		Assert.Empty(TrackerListService.Parse(""));
	}

	private static TrackerListService Service(FakeHandler handler, AppPaths paths) =>
		new(new HttpClient(handler), paths, NullLogger<TrackerListService>.Instance);

	private static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK) =>
		new(code) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

	[Fact]
	public void Missing_or_damaged_cache_reads_as_empty() {
		using var temp = new TempDir();
		var paths = new AppPaths(temp.Path, @"C:\tools");

		Assert.Empty(TrackerListService.ReadCache(paths));
		File.WriteAllText(paths.TrackerListFile, "garbage\n\u0000\n");
		Assert.Empty(TrackerListService.ReadCache(paths));
	}

	[Fact]
	public async Task Refresh_writes_the_cache_and_it_is_not_stale_afterwards() {
		using var temp = new TempDir();
		var paths = new AppPaths(temp.Path, @"C:\tools");
		var service = Service(new FakeHandler((_, _) => Text("udp://a:1/announce\nhttps://b/announce\n")), paths);
		Assert.True(service.IsStale(DateTime.UtcNow));

		var list = await service.RefreshAsync("http://example/list.txt");

		Assert.Equal(["udp://a:1/announce", "https://b/announce"], list);
		Assert.Equal(list, TrackerListService.ReadCache(paths));
		Assert.False(service.IsStale(DateTime.UtcNow));
		Assert.False(File.Exists(paths.TrackerListFile + ".tmp"));
	}

	[Fact]
	public async Task Cache_older_than_a_week_is_stale() {
		using var temp = new TempDir();
		var paths = new AppPaths(temp.Path, @"C:\tools");
		File.WriteAllText(paths.TrackerListFile, "udp://a:1/announce\n");
		var service = Service(new FakeHandler((_, _) => Text("")), paths);

		File.SetLastWriteTimeUtc(paths.TrackerListFile, DateTime.UtcNow.AddDays(-6));
		Assert.False(service.IsStale(DateTime.UtcNow));
		File.SetLastWriteTimeUtc(paths.TrackerListFile, DateTime.UtcNow.AddDays(-8));
		Assert.True(service.IsStale(DateTime.UtcNow));
		await Task.CompletedTask;
	}

	[Theory]
	[InlineData(500, "oops")]
	[InlineData(200, "nothing usable here\n")]
	[InlineData(200, "")]
	public async Task Failed_or_useless_refresh_keeps_the_old_cache(int status, string body) {
		using var temp = new TempDir();
		var paths = new AppPaths(temp.Path, @"C:\tools");
		File.WriteAllText(paths.TrackerListFile, "udp://old:1/announce\n");
		var service = Service(new FakeHandler((_, _) => Text(body, (HttpStatusCode)status)), paths);

		Assert.Null(await service.RefreshAsync("http://example/list.txt"));

		Assert.Equal(["udp://old:1/announce"], TrackerListService.ReadCache(paths));
	}

	[Fact]
	public async Task Network_error_is_reported_as_null_not_thrown() {
		using var temp = new TempDir();
		var service = Service(new FakeHandler((_, _) => throw new HttpRequestException("no network")), new AppPaths(temp.Path, @"C:\tools"));

		Assert.Null(await service.RefreshAsync("http://example/list.txt"));
	}

	[Fact]
	public async Task Oversized_answer_is_ignored() {
		using var temp = new TempDir();
		var paths = new AppPaths(temp.Path, @"C:\tools");
		var huge = string.Join("\n", Enumerable.Range(1, 40_000).Select(i => $"udp://tracker{i}.example.com:6969/announce"));
		var service = Service(new FakeHandler((_, _) => Text(huge)), paths);

		Assert.Null(await service.RefreshAsync("http://example/list.txt"));
		Assert.False(File.Exists(paths.TrackerListFile));
	}
}

public class DiskSpaceTests {
	[Fact]
	public void Existing_folder_reports_free_space() {
		using var temp = new TempDir();

		Assert.True(DiskSpace.FreeBytes(temp.Path) > 0);
	}

	[Fact]
	public void Folder_that_does_not_exist_yet_is_measured_through_its_parent() {
		using var temp = new TempDir();

		Assert.Equal(DiskSpace.FreeBytes(temp.Path) / 1_000_000, DiskSpace.FreeBytes(Path.Combine(temp.Path, "a", "b", "c")) / 1_000_000);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("bad\0path")]
	public void Unusable_paths_give_null(string? folder) {
		Assert.Null(DiskSpace.FreeBytes(folder));
	}

	[Fact]
	public void Fits_blocks_only_when_the_space_is_known_to_be_too_small() {
		using var temp = new TempDir();
		var free = DiskSpace.FreeBytes(temp.Path)!.Value;

		Assert.True(DiskSpace.Fits(0, temp.Path));
		Assert.True(DiskSpace.Fits(1024, temp.Path));
		Assert.False(DiskSpace.Fits(free + 1_000_000_000, temp.Path));
		Assert.True(DiskSpace.Fits(long.MaxValue, "bad\0path")); // unknown free space never blocks
	}
}

[Collection(RealAria2Collection.Name)]
public class TorrentConfigIntegrationTests {
	[RequiresAria2Fact]
	public async Task Started_engine_runs_with_the_bittorrent_options_and_the_cached_trackers() {
		using var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		paths.EnsureDirectories();
		File.WriteAllLines(paths.TrackerListFile, ["udp://a.example:1/announce", "https://b.example/announce"]);
		await using var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance);

		await engine.StartAsync();

		Assert.Equal(EngineState.Running, engine.State);
		var options = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("true", options["bt-save-metadata"]);
		Assert.Equal("true", options["bt-load-saved-metadata"]);
		Assert.Equal("true", options["bt-enable-lpd"]);
		Assert.Equal("none", options["file-allocation"]);
		Assert.Equal("udp://a.example:1/announce,https://b.example/announce", options["bt-tracker"]);

		await engine.Rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["bt-tracker"] = "udp://c.example:1/announce" });
		Assert.Equal("udp://c.example:1/announce", (await engine.Rpc.GetGlobalOptionAsync())["bt-tracker"]);
	}
}
