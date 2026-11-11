using System.Text.Json.Serialization;

namespace Aria2NET.Core.Settings;

public sealed class AppSettings {
	public int SchemaVersion { get; set; } = 1;
	/// <summary>Folder used when nothing else was chosen; null means the user's Downloads folder.</summary>
	public string? DefaultDownloadFolder { get; set; }
	/// <summary>Folder picked in the last Add dialog.</summary>
	public string? LastUsedFolder { get; set; }
	/// <summary>Ask before "Remove and delete file". Cleared by the "Don't ask again" box.</summary>
	public bool ConfirmDeleteFiles { get; set; } = true;
	public string StatusFilter { get; set; } = "All";
	public string SortColumn { get; set; } = "None";
	public bool SortDescending { get; set; }
	/// <summary>How long finished downloads stay in History: 0 keeps them forever, otherwise a number of days (30, 90, 365).</summary>
	public int HistoryRetentionDays { get; set; }
	/// <summary>The one-time offer to open .torrent files and magnet links with Aria2NET has been shown.</summary>
	public bool TorrentAssociationPrompted { get; set; }
	/// <summary>The window's close button hides it to the tray (downloads keep running) instead of exiting.</summary>
	public bool CloseToTray { get; set; } = true;
	/// <summary>Minimizing hides the window to the tray instead of leaving a taskbar button.</summary>
	public bool MinimizeToTray { get; set; }
	/// <summary>Ask before exiting while downloads are still running.</summary>
	public bool ConfirmExitWithActive { get; set; } = true;
	/// <summary>When Windows starts the app at sign-in, keep it in the tray instead of opening the window.</summary>
	public bool StartMinimized { get; set; } = true;
	/// <summary>Pause every download once the engine is up, instead of continuing where the last session stopped.</summary>
	public bool StartPaused { get; set; }
	public bool NotifyOnComplete { get; set; } = true;
	public bool NotifyOnFailed { get; set; } = true;
	public bool NotifyOnTorrentFinished { get; set; } = true;
	public bool NotifySound { get; set; } = true;
	/// <summary>Skip the toast while the Aria2NET window is the active window.</summary>
	public bool NotifyOnlyWhenUnfocused { get; set; }
	/// <summary>Keep Windows from sleeping while something downloads.</summary>
	public bool PreventSleep { get; set; } = true;
	/// <summary>Pause what runs while the PC is on battery, and resume it when it is plugged in again.</summary>
	public bool PauseOnBattery { get; set; }
	/// <summary>Pause what runs while the connection is metered, and resume it when it is not.</summary>
	public bool PauseOnMetered { get; set; }
	/// <summary>Global hotkeys: Ctrl+Alt+A shows or hides the window, Ctrl+Alt+N opens the Add dialog.</summary>
	public bool HotkeysEnabled { get; set; }
	/// <summary>Ask Windows to start the app again (in the tray) if it crashes or stops responding.</summary>
	public bool RestartAfterCrash { get; set; } = true;
	public WindowPlacement? Window { get; set; }

	// Downloads. Defaults match aria2's own, except file allocation (see Aria2Config).
	/// <summary>How many downloads run at once; the rest wait in the queue.</summary>
	public int MaxConcurrentDownloads { get; set; } = 5;
	public int MaxConnectionsPerServer { get; set; } = 1;
	/// <summary>Pieces one download is cut into (connections used for it).</summary>
	public int Split { get; set; } = 5;
	public int MinSplitSizeMiB { get; set; } = 20;
	/// <summary>Attempts per file before it fails; 0 means keep trying.</summary>
	public int MaxTries { get; set; } = 5;
	public int RetryWaitSeconds { get; set; }
	public int ConnectTimeoutSeconds { get; set; } = 60;
	public int TimeoutSeconds { get; set; } = 60;
	/// <summary>Null or empty keeps aria2's own user agent.</summary>
	public string? UserAgent { get; set; }
	public FileAllocationMode FileAllocation { get; set; } = FileAllocationMode.None;

	// Speed. Limits are KiB/s; 0 means unlimited.
	public int DownloadLimitKiBps { get; set; }
	public int UploadLimitKiBps { get; set; }
	public int AltDownloadLimitKiBps { get; set; }
	public int AltUploadLimitKiBps { get; set; }
	/// <summary>The alternative limits are in force instead of the normal ones.</summary>
	public bool AltSpeedActive { get; set; }

	// Network.
	public ProxyMode Proxy { get; set; } = ProxyMode.None;
	/// <summary>Manual proxy as <c>host:port</c> or a full URL such as <c>http://host:8080</c>.</summary>
	public string? ProxyAddress { get; set; }
	public string? ProxyUser { get; set; }
	/// <summary>The proxy password, protected with <see cref="SecretProtector"/>. Never plain text.</summary>
	public string? ProxyPasswordProtected { get; set; }
	/// <summary>Verify HTTPS server certificates. Turning this off makes downloads open to tampering.</summary>
	public bool CheckCertificates { get; set; } = true;
	/// <summary>Use IPv6 as well as IPv4. Off helps on networks where IPv6 is broken.</summary>
	public bool UseIPv6 { get; set; } = true;

	// Torrents. Seeding and peer options apply to torrents added afterwards; ports and DHT need an engine restart.
	/// <summary>First and last port tried for incoming BitTorrent connections.</summary>
	public int ListenPortStart { get; set; } = 6881;
	public int ListenPortEnd { get; set; } = 6999;
	/// <summary>UDP port of the DHT.</summary>
	public int DhtPort { get; set; } = 6881;
	public bool EnableDht { get; set; } = true;
	/// <summary>Peer exchange: peers tell each other about more peers.</summary>
	public bool EnablePex { get; set; } = true;
	/// <summary>Local peer discovery on the LAN.</summary>
	public bool EnableLpd { get; set; } = true;
	public TorrentEncryption Encryption { get; set; } = TorrentEncryption.Allow;
	/// <summary>Stop seeding at this upload/download ratio; 0 means no ratio limit.</summary>
	public double SeedRatio { get; set; } = 1.0;
	/// <summary>Stop seeding after this many minutes; 0 means no time limit.</summary>
	public int SeedTimeMinutes { get; set; }
	public TrackerSource Trackers { get; set; } = TrackerSource.Default;
	/// <summary>Address of the tracker list when <see cref="Trackers"/> is <see cref="TrackerSource.Custom"/>; https only.</summary>
	public string? TrackerUrl { get; set; }

	// General.
	public AppTheme Theme { get; set; } = AppTheme.System;
	/// <summary>What to do when a download would overwrite a file that is already there.</summary>
	public ConflictPolicy Conflict { get; set; } = ConflictPolicy.AutoRename;

	// Updates. Only an installed copy (Setup.exe) can update itself; the portable zip never does.
	/// <summary>Look for a new version in the background a little after start and then twice a day, and download it so it is ready to install.</summary>
	public bool AutoCheckUpdates { get; set; } = true;
	/// <summary>Offer pre-release versions too. Every release below 1.0 is marked as a pre-release on GitHub, so this has to stay on until 1.0.</summary>
	public bool IncludePreReleases { get; set; } = true;

	// Advanced.
	public AppLogLevel LogLevel { get; set; } = AppLogLevel.Information;
	/// <summary>aria2 options typed in by hand (<see cref="RawOptionRules"/>). They are applied last, so they override the settings above.</summary>
	public Dictionary<string, string> RawOptions { get; set; } = [];
}

/// <summary>How much the app log and aria2's log record.</summary>
public enum AppLogLevel {
	Warning,
	Information,
	/// <summary>Everything, including aria2's debug output. Large; for finding a problem.</summary>
	Debug,
}

public enum AppTheme {
	System,
	Light,
	Dark,
}

/// <summary>What happens when the file a download would create already exists.</summary>
public enum ConflictPolicy {
	/// <summary>Keep both: the new file gets a number (name.1.ext).</summary>
	AutoRename,
	Overwrite,
	/// <summary>Ask each time.</summary>
	Ask,
}

public enum TorrentEncryption {
	/// <summary>Encrypted or plain connections, whichever the peer offers.</summary>
	Allow,
	/// <summary>Ask for encryption first, fall back to plain.</summary>
	Prefer,
	/// <summary>Only encrypted connections; fewer peers.</summary>
	Require,
}

public enum TrackerSource {
	/// <summary>The built-in public tracker list.</summary>
	Default,
	Custom,
	/// <summary>No extra trackers: only the ones in each torrent.</summary>
	Off,
}

public enum ProxyMode {
	None,
	/// <summary>The Windows proxy setting, read when the options are applied.</summary>
	System,
	Manual,
}

/// <summary>aria2's <c>file-allocation</c> values.</summary>
public enum FileAllocationMode {
	/// <summary>Files grow as data arrives.</summary>
	None,
	Prealloc,
	Falloc,
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext;
