using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Aria2NET.ViewModels;

public sealed class SettingsViewModel(AppPaths paths, SettingsStore<AppSettings> settings, HistoryService history, StartupRegistration startup, SettingsApplier applier, IAria2Engine engine, ILogger<SettingsViewModel> logger) {
	/// <summary>User-agent presets shown in Settings; the first entry means aria2's own agent and the last is free text.</summary>
	public static IReadOnlyList<(string Label, string? Value)> UserAgentChoices { get; } = [
		("aria2 default", null),
		("Chrome", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"),
		("Firefox", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0"),
		("Edge", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0"),
		("Custom", "")];

	/// <summary>A fresh read of the settings file; the Settings page fills its controls from this.</summary>
	public AppSettings Current => settings.Load();

	/// <summary>Saves a change and pushes it to the running engine right away.</summary>
	public void Update(Action<AppSettings> apply) {
		Change(apply);
		_ = applier.ApplyAsync(settings.Load());
	}

	public void ResetDownloads() {
		var d = new AppSettings();
		Update(s => {
			s.DefaultDownloadFolder = d.DefaultDownloadFolder;
			s.MaxConcurrentDownloads = d.MaxConcurrentDownloads;
			s.MaxConnectionsPerServer = d.MaxConnectionsPerServer;
			s.Split = d.Split;
			s.MinSplitSizeMiB = d.MinSplitSizeMiB;
			s.MaxTries = d.MaxTries;
			s.RetryWaitSeconds = d.RetryWaitSeconds;
			s.ConnectTimeoutSeconds = d.ConnectTimeoutSeconds;
			s.TimeoutSeconds = d.TimeoutSeconds;
			s.UserAgent = d.UserAgent;
			s.FileAllocation = d.FileAllocation;
		});
	}

	public void ResetSpeed() {
		Update(s => {
			s.DownloadLimitKiBps = s.UploadLimitKiBps = s.AltDownloadLimitKiBps = s.AltUploadLimitKiBps = 0;
			s.AltSpeedActive = false;
		});
	}

	/// <summary>The default folder differs from the folder the engine runs in, so a restart is needed.</summary>
	public bool RestartRequired => applier.RestartRequired(settings.Load());

	public Task RestartEngineAsync() => engine.RestartAsync();

	public Task SetAltSpeedAsync(bool active) => applier.SetAltSpeedAsync(active);

	/// <summary>Null when the folder can be used; otherwise a short reason. Creates the folder and writes a test file, so call it off the UI thread.</summary>
	public static string? CheckFolder(string path) {
		try {
			Directory.CreateDirectory(path);
			var probe = Path.Combine(path, $".aria2net-{Guid.NewGuid():N}.tmp");
			File.WriteAllText(probe, "");
			File.Delete(probe);
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			return $"Cannot write to this folder: {ex.Message}";
		}
	}

	/// <summary>Free space on the drive holding <paramref name="path"/>, as text, or empty if unknown.</summary>
	public static string FreeSpaceText(string path) {
		try {
			var root = Path.GetPathRoot(Path.GetFullPath(path));
			return root is null ? "" : $"{FormatBytes(new DriveInfo(root).AvailableFreeSpace)} free";
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) {
			return "";
		}
	}

	private static string FormatBytes(long bytes) {
		string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
		double v = bytes;
		var i = 0;
		while (v >= 1024 && i < units.Length - 1) {
			v /= 1024;
			i++;
		}
		return $"{v:0.#} {units[i]}";
	}

	/// <summary>Days finished downloads stay in History, in the order shown by the Settings combo box (0 = forever).</summary>
	public static IReadOnlyList<(string Label, int Days)> RetentionChoices { get; } = [("Keep forever", 0), ("30 days", 30), ("90 days", 90), ("1 year", 365)];

	public string DataFolder { get; } = paths.DataRoot;
	public string LogsFolder { get; } = paths.LogsDir;

	/// <summary>Ask before "Remove and delete file".</summary>
	public bool ConfirmDeleteFiles {
		get => settings.Load().ConfirmDeleteFiles;
		set {
			try {
				var s = settings.Load();
				s.ConfirmDeleteFiles = value;
				settings.Save(s);
			}
			catch (IOException ex) {
				logger.LogWarning(ex, "Could not save settings");
			}
		}
	}

	/// <summary>The close button hides the window to the tray instead of exiting.</summary>
	public bool CloseToTray {
		get => settings.Load().CloseToTray;
		set => Change(s => s.CloseToTray = value);
	}

	/// <summary>Minimizing hides the window to the tray.</summary>
	public bool MinimizeToTray {
		get => settings.Load().MinimizeToTray;
		set => Change(s => s.MinimizeToTray = value);
	}

	/// <summary>Ask before exiting while downloads are running.</summary>
	public bool ConfirmExitWithActive {
		get => settings.Load().ConfirmExitWithActive;
		set => Change(s => s.ConfirmExitWithActive = value);
	}

	/// <summary>Start Aria2NET when the user signs in to Windows. The Run key is the source of truth, so a manual change there shows up here.</summary>
	public bool StartWithWindows {
		get {
			try {
				return startup.IsEnabled;
			}
			catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) {
				return false;
			}
		}
		set {
			try {
				if (value) {
					startup.Enable();
				}
				else {
					startup.Disable();
				}
			}
			catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
				logger.LogWarning(ex, "Could not change the start-with-Windows entry");
			}
		}
	}

	/// <summary>A start at sign-in stays in the tray instead of opening the window.</summary>
	public bool StartMinimized {
		get => settings.Load().StartMinimized;
		set => Change(s => s.StartMinimized = value);
	}

	/// <summary>Every download is paused when the app starts.</summary>
	public bool StartPaused {
		get => settings.Load().StartPaused;
		set => Change(s => s.StartPaused = value);
	}

	public bool NotifyOnComplete {
		get => settings.Load().NotifyOnComplete;
		set => Change(s => s.NotifyOnComplete = value);
	}

	public bool NotifyOnFailed {
		get => settings.Load().NotifyOnFailed;
		set => Change(s => s.NotifyOnFailed = value);
	}

	public bool NotifyOnTorrentFinished {
		get => settings.Load().NotifyOnTorrentFinished;
		set => Change(s => s.NotifyOnTorrentFinished = value);
	}

	public bool NotifySound {
		get => settings.Load().NotifySound;
		set => Change(s => s.NotifySound = value);
	}

	public bool NotifyOnlyWhenUnfocused {
		get => settings.Load().NotifyOnlyWhenUnfocused;
		set => Change(s => s.NotifyOnlyWhenUnfocused = value);
	}

	public bool PreventSleep {
		get => settings.Load().PreventSleep;
		set => Change(s => s.PreventSleep = value);
	}

	public bool PauseOnBattery {
		get => settings.Load().PauseOnBattery;
		set => Change(s => s.PauseOnBattery = value);
	}

	public bool PauseOnMetered {
		get => settings.Load().PauseOnMetered;
		set => Change(s => s.PauseOnMetered = value);
	}

	public bool HotkeysEnabled {
		get => settings.Load().HotkeysEnabled;
		set => Change(s => s.HotkeysEnabled = value);
	}

	public bool RestartAfterCrash {
		get => settings.Load().RestartAfterCrash;
		set => Change(s => s.RestartAfterCrash = value);
	}

	private void Change(Action<AppSettings> apply) {
		try {
			var s = settings.Load();
			apply(s);
			settings.Save(s);
		}
		catch (IOException ex) {
			logger.LogWarning(ex, "Could not save settings");
		}
	}

	public bool HistoryAvailable => history.IsAvailable;

	/// <summary>Index into <see cref="RetentionChoices"/>; setting it saves and trims History straight away.</summary>
	public int RetentionIndex {
		get {
			var days = settings.Load().HistoryRetentionDays;
			var index = RetentionChoices.ToList().FindIndex(c => c.Days == days);
			return index < 0 ? 0 : index;
		}
		set {
			try {
				var s = settings.Load();
				s.HistoryRetentionDays = RetentionChoices[Math.Clamp(value, 0, RetentionChoices.Count - 1)].Days;
				settings.Save(s);
				history.ApplyRetention(s.HistoryRetentionDays);
			}
			catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) {
				logger.LogWarning(ex, "Could not save the history setting");
			}
		}
	}

	public int ClearHistory() => history.Store?.Clear() ?? 0;
}
