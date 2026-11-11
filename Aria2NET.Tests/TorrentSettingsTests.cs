using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class TorrentOptionsTests {
	[Fact]
	public void Defaults_match_what_aria2_already_does() {
		var o = EngineOptions.TorrentOptions(new AppSettings());
		Assert.Equal("6881-6999", o["listen-port"]);
		Assert.Equal("6881", o["dht-listen-port"]);
		Assert.Equal("true", o["enable-dht"]);
		Assert.Equal("true", o["enable-peer-exchange"]);
		Assert.Equal("true", o["bt-enable-lpd"]);
		Assert.Equal("plain", o["bt-min-crypto-level"]);
		Assert.Equal("false", o["bt-require-crypto"]);
		Assert.Equal("1.0", o["seed-ratio"]);
		Assert.Equal("", o["seed-time"]);
	}

	[Theory]
	[InlineData(TorrentEncryption.Allow, "plain", "false")]
	[InlineData(TorrentEncryption.Prefer, "arc4", "false")]
	[InlineData(TorrentEncryption.Require, "arc4", "true")]
	public void Encryption_maps_to_crypto_level_and_requirement(TorrentEncryption mode, string level, string require) {
		var o = EngineOptions.TorrentOptions(new AppSettings { Encryption = mode });
		Assert.Equal(level, o["bt-min-crypto-level"]);
		Assert.Equal(require, o["bt-require-crypto"]);
	}

	[Fact]
	public void One_port_range_collapses_and_a_reversed_one_is_put_in_order() {
		Assert.Equal("7000", EngineOptions.TorrentOptions(new AppSettings { ListenPortStart = 7000, ListenPortEnd = 7000 })["listen-port"]);
		Assert.Equal("7000-7100", EngineOptions.TorrentOptions(new AppSettings { ListenPortStart = 7100, ListenPortEnd = 7000 })["listen-port"]);
	}

	[Fact]
	public void Seed_rule_and_dht6_follow_the_settings() {
		var o = EngineOptions.TorrentOptions(new AppSettings { SeedRatio = 2.5, SeedTimeMinutes = 90, EnableDht = true, UseIPv6 = false });
		Assert.Equal("2.5", o["seed-ratio"]);
		Assert.Equal("90", o["seed-time"]);
		Assert.Equal("false", o["enable-dht6"]);
	}

	[Fact]
	public void Validation_rejects_bad_ports_and_non_https_tracker_lists() {
		Assert.Null(EngineOptions.ValidateTorrent(new AppSettings()));
		Assert.NotNull(EngineOptions.ValidateTorrent(new AppSettings { ListenPortStart = 80 }));
		Assert.NotNull(EngineOptions.ValidateTorrent(new AppSettings { ListenPortStart = 7000, ListenPortEnd = 6900 }));
		Assert.NotNull(EngineOptions.ValidateTorrent(new AppSettings { Trackers = TrackerSource.Custom, TrackerUrl = "http://x.example/list.txt" }));
		Assert.NotNull(EngineOptions.ValidateTorrent(new AppSettings { Trackers = TrackerSource.Custom }));
		Assert.Null(EngineOptions.ValidateTorrent(new AppSettings { Trackers = TrackerSource.Custom, TrackerUrl = "https://x.example/list.txt" }));
	}

	[Fact]
	public void Tracker_source_picks_the_address_to_fetch() {
		Assert.Equal(TrackerListService.DefaultUrl, TrackerListService.SourceUrl(new AppSettings()));
		Assert.Null(TrackerListService.SourceUrl(new AppSettings { Trackers = TrackerSource.Off }));
		Assert.Equal("https://x.example/l.txt", TrackerListService.SourceUrl(new AppSettings { Trackers = TrackerSource.Custom, TrackerUrl = " https://x.example/l.txt " }));
		Assert.Null(TrackerListService.SourceUrl(new AppSettings { Trackers = TrackerSource.Custom, TrackerUrl = "ftp://x" }));
	}

	[Fact]
	public void Per_torrent_seeding_choices_lift_a_global_time_limit() {
		Assert.Equal(EngineOptions.NoSeedTime, new SeedingOverride(SeedMode.Forever).ToOptions()["seed-time"]);
		Assert.Equal(EngineOptions.NoSeedTime, new SeedingOverride(SeedMode.Ratio, 3).ToOptions()["seed-time"]);
		Assert.Equal("0", new SeedingOverride(SeedMode.Never).ToOptions()["seed-time"]);
	}

	[Fact]
	public void Launch_only_settings_are_listed_so_the_restart_bar_can_catch_them() {
		var live = EngineOptions.LiveOptions(new AppSettings());
		foreach (var key in new[] { "listen-port", "dht-listen-port", "enable-dht", "enable-dht6" }) {
			Assert.Contains(key, EngineOptions.LaunchOnlyKeys);
			Assert.DoesNotContain(key, live.Keys);
		}
		Assert.Contains("seed-time", live.Keys);
	}

	[Fact]
	public void Conf_leaves_out_empty_values_but_keeps_user_values_last() {
		var paths = new AppPaths(@"C:\data", @"C:\tools");
		var text = new Aria2Config(6800, "abc", 42, @"C:\dl", null, EngineOptions.ToGlobalOptions(new AppSettings { SeedRatio = 2.0 })).Render(paths);
		Assert.DoesNotContain("seed-time=", text);
		Assert.DoesNotContain("all-proxy=", text);
		Assert.True(text.LastIndexOf("seed-ratio=2.0", StringComparison.Ordinal) > text.LastIndexOf("seed-ratio=1.0", StringComparison.Ordinal));
	}
}

[Collection(RealAria2Collection.Name)]
public class TorrentSettingsIntegrationTests {
	[RequiresAria2Fact]
	public async Task Peer_and_seeding_options_apply_live_while_ports_and_dht_wait_for_a_restart() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var store = new SettingsStore<AppSettings>(Path.Combine(temp.Path, "settings.json"), AppSettingsJsonContext.Default.AppSettings);
		var applier = new SettingsApplier(engine, store, new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance), NullLogger<SettingsApplier>.Instance);

		var s = new AppSettings { EnablePex = false, EnableLpd = false, Encryption = TorrentEncryption.Require, SeedRatio = 2.5, SeedTimeMinutes = 90, ListenPortStart = 7001, ListenPortEnd = 7010, EnableDht = false };
		Assert.True(await applier.ApplyAsync(s));
		var g = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("false", g["enable-peer-exchange"]);
		Assert.Equal("false", g["bt-enable-lpd"]);
		Assert.Equal("arc4", g["bt-min-crypto-level"]);
		Assert.Equal("true", g["bt-require-crypto"]);
		Assert.Equal("2.5", g["seed-ratio"]);
		Assert.Equal("90", g["seed-time"]);
		Assert.Equal("6881-6999", g["listen-port"]); // launch only
		Assert.Equal("true", g["enable-dht"]);

		Assert.True(await applier.ApplyAsync(new AppSettings()));
		g = await engine.Rpc.GetGlobalOptionAsync();
		Assert.Equal("", g.GetValueOrDefault("seed-time", ""));

		// "Forever" per torrent must be accepted next to a global time limit.
		await engine.Rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["seed-time"] = EngineOptions.NoSeedTime });
		Assert.Equal(EngineOptions.NoSeedTime, (await engine.Rpc.GetGlobalOptionAsync())["seed-time"]);
	}

	[RequiresAria2Fact]
	public async Task Ports_dht_and_seed_rule_take_effect_when_the_engine_starts() {
		using var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		var settings = new AppSettings { ListenPortStart = 7001, ListenPortEnd = 7010, DhtPort = 7011, EnableDht = false, SeedRatio = 3.0, SeedTimeMinutes = 45 };
		await using var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance, () => settings);
		await engine.StartAsync();

		var g = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("7001-7010", g["listen-port"]);
		Assert.Equal("7011", g["dht-listen-port"]);
		Assert.Equal("false", g["enable-dht"]);
		Assert.Equal("3.0", g["seed-ratio"]); // the user's value wins over the built-in default line
		Assert.Equal("45", g["seed-time"]);
	}
}
