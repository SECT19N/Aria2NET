using System.Text.Json;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Logging;
using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class RawOptionRulesTests {
	[Theory]
	[InlineData("bt-max-peers", "80")]
	[InlineData("lowest-speed-limit", "20K")]
	[InlineData("user-agent", "my agent")]
	public void Ordinary_options_are_allowed(string key, string value) => Assert.Null(RawOptionRules.Validate(key, value));

	[Theory]
	[InlineData("rpc-secret", "x")]
	[InlineData("rpc-listen-port", "1")]
	[InlineData("on-download-complete", "calc.exe")]
	[InlineData("on-bt-download-complete", "calc.exe")]
	[InlineData("dir", @"C:\x")]
	[InlineData("input-file", "x")]
	[InlineData("log", "x")]
	[InlineData("save-session", "x")]
	[InlineData("stop-with-process", "1")]
	[InlineData("server-stat-of", "x")]
	[InlineData("http-passwd", "x")]
	[InlineData("ftp-user", "x")]
	[InlineData("all-proxy-passwd", "x")]
	public void Managed_dangerous_and_login_options_are_refused(string key, string value) => Assert.NotNull(RawOptionRules.Validate(key, value));

	[Theory]
	[InlineData("Split", "5")]
	[InlineData("two words", "5")]
	[InlineData("", "5")]
	[InlineData("-split", "5")]
	public void Names_must_look_like_aria2_options(string key, string value) => Assert.NotNull(RawOptionRules.Validate(key, value));

	[Fact]
	public void A_line_break_in_a_value_is_refused() {
		Assert.NotNull(RawOptionRules.Validate("bt-max-peers", "80\nrpc-secret=x"));
		Assert.NotNull(RawOptionRules.Validate("bt-max-peers", new string('1', RawOptionRules.MaxValueLength + 1)));
	}

	[Fact]
	public void Text_is_parsed_line_by_line_with_comments_and_the_line_number_in_errors() {
		var (options, error) = RawOptionRules.Parse("# tweaks\r\nbt-max-peers = 80\r\n\r\nlowest-speed-limit=20K");
		Assert.Null(error);
		Assert.Equal("80", options["bt-max-peers"]);
		Assert.Equal("20K", options["lowest-speed-limit"]);

		Assert.Contains("Line 2", RawOptionRules.Parse("ok=1\nnot a pair").Error);
		Assert.Contains("Line 1", RawOptionRules.Parse("on-download-complete=x").Error);
	}

	[Fact]
	public void The_text_form_round_trips() {
		var options = new Dictionary<string, string> { ["b-opt"] = "2", ["a-opt"] = "1" };
		Assert.Equal("a-opt=1\r\nb-opt=2", RawOptionRules.ToText(options));
		Assert.Equal(options, RawOptionRules.Parse(RawOptionRules.ToText(options)).Options);
	}

	[Fact]
	public void Raw_options_override_the_settings_but_forbidden_ones_in_a_hand_edited_file_are_dropped() {
		var s = new AppSettings { Split = 5, RawOptions = new() { ["split"] = "16", ["on-download-complete"] = "calc.exe", ["rpc-secret"] = "x" } };
		var all = EngineOptions.ToGlobalOptions(s);
		Assert.Equal("16", all["split"]);
		Assert.DoesNotContain("on-download-complete", all.Keys);
		Assert.DoesNotContain("rpc-secret", all.Keys);
	}
}

public class LogLevelTests {
	[Theory]
	[InlineData(AppLogLevel.Warning, "warn", LogLevel.Warning)]
	[InlineData(AppLogLevel.Information, "notice", LogLevel.Information)]
	[InlineData(AppLogLevel.Debug, "debug", LogLevel.Debug)]
	public void Level_maps_to_aria2_and_to_the_app_log(AppLogLevel level, string aria2, LogLevel app) {
		Assert.Equal(aria2, EngineOptions.Aria2LogLevel(level));
		Assert.Equal(app, EngineOptions.AppLogLevelFor(level));
		Assert.Equal(aria2, EngineOptions.LiveOptions(new AppSettings { LogLevel = level })["log-level"]);
	}

	[Fact]
	public void The_file_logger_level_can_change_while_running() {
		using var temp = new TempDir();
		using var provider = new FileLoggerProvider(temp.Path, LogLevel.Warning);
		var logger = provider.CreateLogger("t");
		Assert.False(logger.IsEnabled(LogLevel.Information));

		provider.MinLevel = LogLevel.Debug;
		Assert.True(logger.IsEnabled(LogLevel.Debug));
		logger.LogDebug("now visible");
		Assert.Contains("now visible", File.ReadAllText(Directory.GetFiles(temp.Path, "aria2net-*.log").Single()));

		provider.MinLevel = LogLevel.Error;
		Assert.False(logger.IsEnabled(LogLevel.Warning));
	}
}

public class SettingsTransferTests {
	private static AppSettings Machine() => new() {
		Window = new WindowPlacement(), LastUsedFolder = @"D:\here", TorrentAssociationPrompted = true, ProxyPasswordProtected = SecretProtector.Protect("pw"),
	};

	[Fact]
	public void Export_leaves_out_machine_state_and_secrets() {
		var s = Machine();
		s.Theme = AppTheme.Dark;
		s.ProxyUser = "bob";
		var json = SettingsTransfer.Export(s);

		Assert.DoesNotContain("D:\\\\here", json);
		Assert.DoesNotContain(s.ProxyPasswordProtected!, json);
		using var doc = JsonDocument.Parse(json);
		Assert.Equal("bob", doc.RootElement.GetProperty("ProxyUser").GetString());
		Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Window").ValueKind);
	}

	[Fact]
	public void Import_round_trips_preferences_and_keeps_this_pcs_machine_state() {
		var source = new AppSettings { Theme = AppTheme.Light, Split = 9, SeedRatio = 2.5, RawOptions = new() { ["bt-max-peers"] = "80" }, Conflict = ConflictPolicy.Ask };
		var current = Machine();

		var (imported, error) = SettingsTransfer.Import(SettingsTransfer.Export(source), current);

		Assert.Null(error);
		Assert.Equal(AppTheme.Light, imported!.Theme);
		Assert.Equal(9, imported.Split);
		Assert.Equal("80", imported.RawOptions["bt-max-peers"]);
		Assert.Equal(ConflictPolicy.Ask, imported.Conflict);
		Assert.Equal(@"D:\here", imported.LastUsedFolder);
		Assert.True(imported.TorrentAssociationPrompted);
		Assert.Equal(current.ProxyPasswordProtected, imported.ProxyPasswordProtected);
	}

	[Fact]
	public void Out_of_range_numbers_are_brought_into_range() {
		var (imported, error) = SettingsTransfer.Import("""{ "SchemaVersion": 1, "MaxConcurrentDownloads": 9999, "Split": -3, "TimeoutSeconds": 0, "SeedRatio": 1e9 }""", new AppSettings());
		Assert.Null(error);
		Assert.Equal(50, imported!.MaxConcurrentDownloads);
		Assert.Equal(1, imported.Split);
		Assert.Equal(1, imported.TimeoutSeconds);
		Assert.Equal(1000, imported.SeedRatio);
	}

	[Theory]
	[InlineData("not json at all")]
	[InlineData("[]")]
	[InlineData("""{ "SchemaVersion": 2 }""")]
	[InlineData("""{ "SchemaVersion": 1, "Theme": 42 }""")]
	[InlineData("""{ "SchemaVersion": 1, "RawOptions": { "on-download-complete": "calc.exe" } }""")]
	[InlineData("""{ "SchemaVersion": 1, "Proxy": 2, "ProxyAddress": "not a proxy:99999" }""")]
	[InlineData("""{ "SchemaVersion": 1, "Trackers": 1, "TrackerUrl": "http://insecure.example/list" }""")]
	[InlineData("""{ "SchemaVersion": 1, "UserAgent": "x\nrpc-secret=1" }""")]
	[InlineData("""{ "SchemaVersion": 1, "DefaultDownloadFolder": "C:\\x\ndir=D:\\y" }""")]
	[InlineData("""{ "SchemaVersion": 1, "ListenPortStart": 80 }""")]
	public void Harmful_or_unknown_files_are_refused_whole(string json) {
		var (imported, error) = SettingsTransfer.Import(json, new AppSettings());
		Assert.Null(imported);
		Assert.False(string.IsNullOrEmpty(error));
	}

	[Fact]
	public void An_oversized_file_is_refused() {
		var (imported, error) = SettingsTransfer.Import(new string(' ', SettingsTransfer.MaxImportBytes + 1), new AppSettings());
		Assert.Null(imported);
		Assert.NotNull(error);
	}

	[Fact]
	public void Reset_all_restores_defaults_but_keeps_machine_state_and_drops_the_proxy_password() {
		var current = Machine();
		current.Theme = AppTheme.Dark;
		current.Split = 12;
		current.RawOptions["bt-max-peers"] = "80";

		var fresh = SettingsTransfer.ResetAll(current);

		Assert.Equal(AppTheme.System, fresh.Theme);
		Assert.Equal(5, fresh.Split);
		Assert.Empty(fresh.RawOptions);
		Assert.Equal(@"D:\here", fresh.LastUsedFolder);
		Assert.True(fresh.TorrentAssociationPrompted);
		Assert.NotNull(fresh.Window);
		Assert.Null(fresh.ProxyPasswordProtected);
	}
}

public class ConfInjectionTests {
	[Fact]
	public void A_control_character_never_reaches_the_conf_file() {
		var paths = new AppPaths(@"C:\data", @"C:\tools");
		var options = new Dictionary<string, string> { ["user-agent"] = "ok\nrpc-secret=stolen", ["split"] = "4" };
		var text = new Aria2Config(6800, "abc", 42, @"C:\dl", null, options).Render(paths);
		Assert.DoesNotContain("stolen", text);
		Assert.Contains("split=4", text);
		Assert.Single(text.Split('\n'), l => l.StartsWith("rpc-secret=", StringComparison.Ordinal));
	}

	[Fact]
	public void A_default_folder_with_a_line_break_falls_back_to_the_downloads_folder() {
		Assert.Equal(DefaultFolderResolver.UserDownloads(), DefaultFolderResolver.ForEngine(new AppSettings { DefaultDownloadFolder = "C:\\x\ndir=D:\\y" }));
		Assert.Equal(EngineOptions.DefaultUserAgent, EngineOptions.ToGlobalOptions(new AppSettings { UserAgent = "a\nb" })["user-agent"]);
	}
}

[Collection(RealAria2Collection.Name)]
public class AdvancedSettingsIntegrationTests {
	[RequiresAria2Fact]
	public async Task Raw_options_and_log_level_apply_and_a_refused_option_does_not_stop_the_rest() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var store = new SettingsStore<AppSettings>(Path.Combine(temp.Path, "settings.json"), AppSettingsJsonContext.Default.AppSettings);
		var applier = new SettingsApplier(engine, store, new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance), NullLogger<SettingsApplier>.Instance);

		var s = new AppSettings { LogLevel = AppLogLevel.Debug, Split = 7, RawOptions = new() { ["bt-max-peers"] = "81", ["max-tries"] = "not-a-number", ["lowest-speed-limit"] = "20K" } };
		var all = await applier.ApplyAsync(s);

		var g = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.False(all); // aria2 refused the unparsable value (it silently accepts names it does not know) ...
		Assert.Equal("debug", g["log-level"]); // ... but everything else went through
		Assert.Equal("7", g["split"]);
		Assert.Equal("81", g["bt-max-peers"]);
		Assert.Equal("20480", g["lowest-speed-limit"]);
	}

	[RequiresAria2Fact]
	public async Task Raw_options_are_part_of_the_conf_at_launch() {
		using var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		var settings = new AppSettings { RawOptions = new() { ["bt-max-peers"] = "66" }, LogLevel = AppLogLevel.Warning };
		await using var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance, () => settings);
		await engine.StartAsync();

		var g = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("66", g["bt-max-peers"]);
		Assert.Equal("warn", g["log-level"]);
	}

	[RequiresAria2Fact]
	public async Task Name_clash_options_are_accepted_per_download_and_globally() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var store = new SettingsStore<AppSettings>(Path.Combine(temp.Path, "settings.json"), AppSettingsJsonContext.Default.AppSettings);
		var applier = new SettingsApplier(engine, store, new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance), NullLogger<SettingsApplier>.Instance);

		Assert.True(await applier.ApplyAsync(new AppSettings { Conflict = ConflictPolicy.Overwrite }));
		var g = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("true", g["allow-overwrite"]);
		Assert.Equal("false", g["auto-file-renaming"]);
	}
}
