using Aria2NET.Core;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Aria2NET.ViewModels;

public sealed class SettingsViewModel(AppPaths paths, SettingsStore<AppSettings> settings, HistoryService history, StartupRegistration startup, ILogger<SettingsViewModel> logger) {
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
