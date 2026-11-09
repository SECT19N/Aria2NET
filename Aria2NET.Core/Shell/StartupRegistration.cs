using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Aria2NET.Core.Shell;

/// <summary>
/// Starts Aria2NET when the user signs in to Windows, through the per-user Run key (HKCU only, no admin rights). The entry carries
/// <see cref="LaunchArguments.AutostartArgument"/> so the app can tell a sign-in start from a normal launch. There is no installer, so the exe
/// path can change when the folder is moved; <see cref="Refresh"/> repairs the entry at launch.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupRegistration(string exePath, RegistryKey? software = null) {
	private const string RunKey = @"Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "Aria2NET";

	/// <summary>HKCU\Software, or a throwaway key in tests.</summary>
	private readonly RegistryKey _software = software ?? Registry.CurrentUser.OpenSubKey("Software", writable: true)!;

	private string Command => $"\"{exePath}\" {LaunchArguments.AutostartArgument}";

	public bool IsEnabled => Current() is not null;

	public void Enable() {
		using var key = _software.CreateSubKey(RunKey, writable: true);
		key.SetValue(ValueName, Command);
	}

	/// <summary>Removes the entry. Safe to call when it is not there.</summary>
	public void Disable() {
		using var key = _software.OpenSubKey(RunKey, writable: true);
		key?.DeleteValue(ValueName, throwOnMissingValue: false);
	}

	/// <summary>Rewrites an existing entry that points somewhere else (the app was moved). Never creates one. Returns whether it changed anything.</summary>
	public bool Refresh() {
		if (Current() is { } current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase)) {
			Enable();
			return true;
		}
		return false;
	}

	private string? Current() {
		using var key = _software.OpenSubKey(RunKey);
		return key?.GetValue(ValueName) as string;
	}
}
