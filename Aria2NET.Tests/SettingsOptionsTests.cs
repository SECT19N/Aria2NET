using System.Text.Json;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class EngineOptionsTests {
	[Fact]
	public void Normal_limits_apply_when_alt_speeds_are_off() {
		var s = new AppSettings { DownloadLimitKiBps = 500, UploadLimitKiBps = 0, AltDownloadLimitKiBps = 50, AltUploadLimitKiBps = 10 };
		var o = EngineOptions.ToGlobalOptions(s);
		Assert.Equal("500K", o["max-overall-download-limit"]);
		Assert.Equal("0", o["max-overall-upload-limit"]);
	}

	[Fact]
	public void Alt_limits_replace_the_normal_ones_when_active() {
		var s = new AppSettings { DownloadLimitKiBps = 500, UploadLimitKiBps = 100, AltDownloadLimitKiBps = 50, AltUploadLimitKiBps = 0, AltSpeedActive = true };
		var o = EngineOptions.SpeedOptions(s);
		Assert.Equal("50K", o["max-overall-download-limit"]);
		Assert.Equal("0", o["max-overall-upload-limit"]);
		Assert.Equal(2, o.Count);
	}

	[Fact]
	public void Download_options_use_aria2_names_and_clamp_bad_values() {
		var s = new AppSettings { MaxConcurrentDownloads = 0, MaxConnectionsPerServer = 99, MinSplitSizeMiB = 20, FileAllocation = FileAllocationMode.Falloc, UserAgent = "  " };
		var o = EngineOptions.ToGlobalOptions(s);
		Assert.Equal("1", o["max-concurrent-downloads"]);
		Assert.Equal("16", o["max-connection-per-server"]);
		Assert.Equal("20M", o["min-split-size"]);
		Assert.Equal("falloc", o["file-allocation"]);
		Assert.Equal(EngineOptions.DefaultUserAgent, o["user-agent"]);
	}

	[Fact]
	public void Settings_file_from_before_0_7_loads_with_defaults() {
		var s = JsonSerializer.Deserialize("""{ "SchemaVersion": 1, "CloseToTray": false }""", AppSettingsJsonContext.Default.AppSettings)!;
		Assert.False(s.CloseToTray);
		Assert.Equal(5, s.MaxConcurrentDownloads);
		Assert.Equal(FileAllocationMode.None, s.FileAllocation);
		Assert.False(s.AltSpeedActive);
	}

	[Fact]
	public void Config_renders_user_options_after_the_defaults() {
		var paths = new AppPaths(@"C:\data", @"C:\tools");
		var options = new Dictionary<string, string> { ["split"] = "8", ["file-allocation"] = "prealloc" };
		var text = new Aria2Config(6800, "abc", 42, @"C:\dl", null, options).Render(paths);
		Assert.Contains("split=8", text);
		Assert.True(text.LastIndexOf("file-allocation=prealloc", StringComparison.Ordinal) > text.LastIndexOf("file-allocation=none", StringComparison.Ordinal));
	}

	[Fact]
	public void Default_folder_resolves_in_the_documented_order() {
		Assert.Equal(DefaultFolderResolver.UserDownloads(), DefaultFolderResolver.ForEngine(new AppSettings()));
		Assert.Equal(@"D:\dl", DefaultFolderResolver.ForEngine(new AppSettings { DefaultDownloadFolder = @"D:\dl" }));
		Assert.Equal(@"E:\last", DefaultFolderResolver.ForAddDialog(new AppSettings { DefaultDownloadFolder = @"D:\dl", LastUsedFolder = @"E:\last" }));
		Assert.Equal(@"D:\dl", DefaultFolderResolver.ForAddDialog(new AppSettings { DefaultDownloadFolder = @"D:\dl" }));
	}
}

[Collection(RealAria2Collection.Name)]
public class SettingsApplierIntegrationTests {
	[RequiresAria2Fact]
	public async Task Applies_settings_live_and_flips_alt_speeds() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var store = new SettingsStore<AppSettings>(Path.Combine(temp.Path, "settings.json"), AppSettingsJsonContext.Default.AppSettings);
		var ops = new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance);
		var applier = new SettingsApplier(engine, store, ops, NullLogger<SettingsApplier>.Instance);

		var s = new AppSettings { Split = 9, MaxConcurrentDownloads = 3, DownloadLimitKiBps = 400, AltDownloadLimitKiBps = 40, AltUploadLimitKiBps = 4 };
		store.Save(s);
		Assert.True(await applier.ApplyAsync(s));
		var global = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("9", global["split"]);
		Assert.Equal("3", global["max-concurrent-downloads"]);
		Assert.Equal(400 * 1024, long.Parse(global["max-overall-download-limit"]));

		await applier.SetAltSpeedAsync(true);
		global = await engine.Rpc.GetGlobalOptionAsync();
		Assert.Equal(40 * 1024, long.Parse(global["max-overall-download-limit"]));
		Assert.Equal(4 * 1024, long.Parse(global["max-overall-upload-limit"]));
		Assert.True(store.Load().AltSpeedActive);

		await applier.SetAltSpeedAsync(false);
		global = await engine.Rpc.GetGlobalOptionAsync();
		Assert.Equal(400 * 1024, long.Parse(global["max-overall-download-limit"]));
	}
}

public class NetworkOptionsTests {
	[Fact]
	public void Defaults_mean_no_proxy_certificates_checked_and_ipv6_on() {
		var o = EngineOptions.NetworkOptions(new AppSettings());
		Assert.Equal("", o["all-proxy"]);
		Assert.Equal("true", o["check-certificate"]);
		Assert.Equal("false", o["disable-ipv6"]);
	}

	[Fact]
	public void Manual_proxy_sends_address_and_decrypted_credentials() {
		var s = new AppSettings { Proxy = ProxyMode.Manual, ProxyAddress = " proxy.local:8080 ", ProxyUser = "bob", ProxyPasswordProtected = SecretProtector.Protect("p@ss") };
		var o = EngineOptions.NetworkOptions(s);
		Assert.Equal("proxy.local:8080", o["all-proxy"]);
		Assert.Equal("bob", o["all-proxy-user"]);
		Assert.Equal("p@ss", o["all-proxy-passwd"]);
		Assert.DoesNotContain("p@ss", s.ProxyPasswordProtected);
	}

	[Fact]
	public void Invalid_manual_address_is_not_sent_and_is_reported() {
		var s = new AppSettings { Proxy = ProxyMode.Manual, ProxyAddress = "not a host:99999" };
		Assert.NotNull(EngineOptions.ValidateProxy(s));
		Assert.Equal("", EngineOptions.NetworkOptions(s)["all-proxy"]);
		Assert.NotNull(EngineOptions.ValidateProxy(new AppSettings { Proxy = ProxyMode.Manual }));
		Assert.Null(EngineOptions.ValidateProxy(new AppSettings { Proxy = ProxyMode.Manual, ProxyAddress = "http://proxy.local:3128" }));
	}

	[Fact]
	public void System_proxy_uses_what_windows_reports_and_none_means_direct() {
		var s = new AppSettings { Proxy = ProxyMode.System };
		Assert.Equal("http://corp:8080", EngineOptions.NetworkOptions(s, () => new Uri("http://corp:8080/"))["all-proxy"]);
		Assert.Equal("", EngineOptions.NetworkOptions(s, () => null)["all-proxy"]);
	}

	[Fact]
	public void Turned_off_toggles_map_to_aria2_values() {
		var o = EngineOptions.NetworkOptions(new AppSettings { CheckCertificates = false, UseIPv6 = false });
		Assert.Equal("false", o["check-certificate"]);
		Assert.Equal("true", o["disable-ipv6"]);
	}

	[Fact]
	public void Secret_round_trips_and_garbage_reads_as_empty() {
		Assert.Equal("", SecretProtector.Protect(""));
		Assert.Equal("héllo wörld", SecretProtector.Unprotect(SecretProtector.Protect("héllo wörld")));
		Assert.Equal("", SecretProtector.Unprotect("!!not base64!!"));
		Assert.Equal("", SecretProtector.Unprotect(null));
	}
}

[Collection(RealAria2Collection.Name)]
public class NetworkApplyIntegrationTests {
	[RequiresAria2Fact]
	public async Task Aria2_accepts_the_network_options_on_a_running_engine() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var store = new SettingsStore<AppSettings>(Path.Combine(temp.Path, "settings.json"), AppSettingsJsonContext.Default.AppSettings);
		var applier = new SettingsApplier(engine, store, new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance), NullLogger<SettingsApplier>.Instance);

		var s = new AppSettings { Proxy = ProxyMode.Manual, ProxyAddress = "127.0.0.1:3128", ProxyUser = "u", CheckCertificates = false, UseIPv6 = false };
		Assert.True(await applier.ApplyAsync(s));
		var global = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Contains("127.0.0.1:3128", global["all-proxy"]); // aria2 normalizes it to http://127.0.0.1:3128/
		// aria2 ignores these two on a running engine, so they are not sent live and need a restart.
		Assert.Equal("true", global["check-certificate"]);
		Assert.Equal("false", global["disable-ipv6"]);

		Assert.True(await applier.ApplyAsync(new AppSettings()));
		global = await engine.Rpc.GetGlobalOptionAsync();
		Assert.Equal("", global.GetValueOrDefault("all-proxy", ""));
	}

	[RequiresAria2Fact]
	public async Task Launch_only_options_take_effect_when_the_engine_starts() {
		using var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		var settings = new AppSettings { CheckCertificates = false, UseIPv6 = false };
		await using var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance, () => settings);
		await engine.StartAsync();

		var global = await engine.Rpc!.GetGlobalOptionAsync();
		Assert.Equal("false", global["check-certificate"]);
		Assert.Equal("true", global["disable-ipv6"]);
	}

	[Fact]
	public void Live_options_leave_out_the_launch_only_ones_but_the_conf_keeps_them() {
		var s = new AppSettings { CheckCertificates = false, UseIPv6 = false };
		var live = EngineOptions.LiveOptions(s);
		Assert.DoesNotContain("check-certificate", live.Keys);
		Assert.DoesNotContain("disable-ipv6", live.Keys);
		Assert.Contains("all-proxy", live.Keys);
		var all = EngineOptions.ToGlobalOptions(s);
		Assert.Equal("false", all["check-certificate"]);
		Assert.Equal("true", all["disable-ipv6"]);
	}
}
