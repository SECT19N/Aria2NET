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
