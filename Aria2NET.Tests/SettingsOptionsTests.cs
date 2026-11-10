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
