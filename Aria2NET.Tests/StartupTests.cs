using System.Runtime.Versioning;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Microsoft.Win32;

namespace Aria2NET.Tests;

[SupportedOSPlatform("windows")]
public sealed class StartupRegistrationTests : IDisposable {
	private const string Exe = @"C:\Apps\Aria2NET\Aria2NET.exe";
	private const string RunKey = @"Microsoft\Windows\CurrentVersion\Run";
	private readonly string _path = @"Software\Aria2NET-Tests\" + Guid.NewGuid().ToString("N");
	private readonly RegistryKey _software;

	public StartupRegistrationTests() {
		_software = Registry.CurrentUser.CreateSubKey(_path, writable: true);
	}

	public void Dispose() {
		_software.Dispose();
		Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
	}

	private StartupRegistration Create(string exe = Exe) => new(exe, _software);

	private string? Stored() {
		using var key = _software.OpenSubKey(RunKey);
		return key?.GetValue("Aria2NET") as string;
	}

	[Fact]
	public void Nothing_is_enabled_until_asked() {
		Assert.False(Create().IsEnabled);
	}

	[Fact]
	public void Enable_writes_the_quoted_path_with_the_autostart_flag() {
		var startup = Create();

		startup.Enable();

		Assert.True(startup.IsEnabled);
		Assert.Equal($"\"{Exe}\" --autostart", Stored());
	}

	[Fact]
	public void Disable_removes_the_entry_and_is_safe_when_there_is_none() {
		var startup = Create();
		startup.Disable();

		startup.Enable();
		startup.Disable();

		Assert.False(startup.IsEnabled);
		Assert.Null(Stored());
	}

	[Fact]
	public void Disable_leaves_other_programs_run_entries_alone() {
		using (var run = _software.CreateSubKey(RunKey, writable: true)) {
			run.SetValue("Other", "other.exe");
		}
		var startup = Create();
		startup.Enable();

		startup.Disable();

		using var after = _software.OpenSubKey(RunKey)!;
		Assert.Equal("other.exe", after.GetValue("Other"));
	}

	[Fact]
	public void Refresh_repairs_an_entry_that_points_at_the_old_location() {
		Create(@"C:\Old\Aria2NET.exe").Enable();

		var changed = Create().Refresh();

		Assert.True(changed);
		Assert.Equal($"\"{Exe}\" --autostart", Stored());
	}

	[Fact]
	public void Refresh_does_nothing_when_the_entry_is_current_or_absent() {
		var startup = Create();
		Assert.False(startup.Refresh());
		Assert.False(startup.IsEnabled); // never creates an entry the user did not ask for

		startup.Enable();
		Assert.False(startup.Refresh());
	}
}

public class AutostartArgumentTests {
	[Fact]
	public void The_flag_is_recognised_in_any_case_among_other_arguments() {
		Assert.True(LaunchArguments.IsAutostart(["--autostart"]));
		Assert.True(LaunchArguments.IsAutostart(["magnet:?xt=urn:btih:abc", "--AutoStart"]));
		Assert.False(LaunchArguments.IsAutostart([]));
		Assert.False(LaunchArguments.IsAutostart(["--other"]));
	}

	[Fact]
	public void The_flag_is_not_mistaken_for_a_download() {
		Assert.Empty(LaunchArguments.Extract(["--autostart"]));
	}

	[Fact]
	public void Starting_with_the_tray_only_is_the_default_and_pausing_at_start_is_not() {
		var settings = new AppSettings();

		Assert.True(settings.StartMinimized);
		Assert.False(settings.StartPaused);
	}
}
