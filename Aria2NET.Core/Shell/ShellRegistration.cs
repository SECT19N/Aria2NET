using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Aria2NET.Core.Shell;

public sealed record ShellRegistrationState(bool Registered, bool DefaultForTorrent, bool DefaultForMagnet);

/// <summary>
/// Makes Aria2NET a candidate for .torrent, .metalink, .meta4 files and magnet links, for the current user only (no admin rights, nothing
/// outside HKCU). Windows 10/11 protects the user's choice of default app, so this only <em>offers</em> the app in Settings &gt; Default apps;
/// <see cref="GetState"/> says whether the user has picked it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellRegistration(string exePath, RegistryKey? software = null, bool notifyShell = true) {
	public const string TorrentProgId = "Aria2NET.Torrent";
	public const string MetalinkProgId = "Aria2NET.Metalink";
	public const string MagnetProgId = "Aria2NET.Magnet";
	private const string AppKey = "Aria2NET";

	private static readonly (string Extension, string ProgId)[] Files = [(".torrent", TorrentProgId), (".metalink", MetalinkProgId), (".meta4", MetalinkProgId)];

	/// <summary>HKCU\Software, or a throwaway key in tests.</summary>
	private readonly RegistryKey _software = software ?? Registry.CurrentUser.OpenSubKey("Software", writable: true)!;

	private string Command => $"\"{exePath}\" \"%1\"";

	public void Register() {
		using var classes = _software.CreateSubKey("Classes", writable: true);
		WriteProgId(classes, TorrentProgId, "BitTorrent file");
		WriteProgId(classes, MetalinkProgId, "Metalink file");
		WriteProgId(classes, MagnetProgId, "Magnet link", urlProtocol: true);

		foreach (var (extension, progId) in Files) {
			using var key = classes.CreateSubKey($@"{extension}\OpenWithProgids", writable: true);
			key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
		}

		using (var caps = _software.CreateSubKey($@"{AppKey}\Capabilities", writable: true)) {
			caps.SetValue("ApplicationName", "Aria2NET");
			caps.SetValue("ApplicationDescription", "Download manager for links, torrents and magnet links");
			using var fileAssociations = caps.CreateSubKey("FileAssociations", writable: true);
			foreach (var (extension, progId) in Files) {
				fileAssociations.SetValue(extension, progId);
			}
			using var urlAssociations = caps.CreateSubKey("URLAssociations", writable: true);
			urlAssociations.SetValue("magnet", MagnetProgId);
		}
		using (var registered = _software.CreateSubKey("RegisteredApplications", writable: true)) {
			registered.SetValue(AppKey, $@"Software\{AppKey}\Capabilities");
		}
		NotifyShell();
	}

	/// <summary>Removes everything <see cref="Register"/> wrote. Safe to call when nothing is registered.</summary>
	public void Unregister() {
		using (var classes = _software.OpenSubKey("Classes", writable: true)) {
			if (classes is not null) {
				foreach (var progId in new[] { TorrentProgId, MetalinkProgId, MagnetProgId }) {
					classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
				}
				foreach (var (extension, progId) in Files) {
					using var key = classes.OpenSubKey($@"{extension}\OpenWithProgids", writable: true);
					key?.DeleteValue(progId, throwOnMissingValue: false);
				}
			}
		}
		using (var registered = _software.OpenSubKey("RegisteredApplications", writable: true)) {
			registered?.DeleteValue(AppKey, throwOnMissingValue: false);
		}
		_software.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
		NotifyShell();
	}

	public ShellRegistrationState GetState() {
		using var command = _software.OpenSubKey($@"Classes\{TorrentProgId}\shell\open\command");
		var registered = string.Equals(command?.GetValue("") as string, Command, StringComparison.OrdinalIgnoreCase)
			&& _software.OpenSubKey($@"{AppKey}\Capabilities") is { } caps && Dispose(caps);
		return new ShellRegistrationState(
			registered,
			UserChoice($@"Microsoft\Windows\CurrentVersion\Explorer\FileExts\.torrent\UserChoice") == TorrentProgId,
			UserChoice(@"Microsoft\Windows\Shell\Associations\UrlAssociations\magnet\UserChoice") == MagnetProgId);
	}

	private string? UserChoice(string path) {
		using var key = _software.OpenSubKey(path);
		return key?.GetValue("ProgId") as string;
	}

	private static bool Dispose(RegistryKey key) {
		key.Dispose();
		return true;
	}

	private void WriteProgId(RegistryKey classes, string progId, string description, bool urlProtocol = false) {
		using var key = classes.CreateSubKey(progId, writable: true);
		key.SetValue("", description);
		if (urlProtocol) {
			key.SetValue("URL Protocol", "");
		}
		using (var icon = key.CreateSubKey("DefaultIcon", writable: true)) {
			icon.SetValue("", $"\"{exePath}\",0");
		}
		using var command = key.CreateSubKey(@"shell\open\command", writable: true);
		command.SetValue("", Command);
	}

	private void NotifyShell() {
		if (notifyShell) {
			SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
		}
	}

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
