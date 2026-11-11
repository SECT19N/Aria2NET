using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Aria2NET.ViewModels;

public sealed class SettingsViewModel(AppPaths paths, SettingsStore<AppSettings> settings, HistoryService history, StartupRegistration startup, SettingsApplier applier, IAria2Engine engine, Aria2NET.Core.Logging.FileLoggerProvider fileLog, ILogger<SettingsViewModel> logger) {
	/// <summary>User-agent presets shown in Settings; the first entry means aria2's own agent and the last is free text.</summary>
	public static IReadOnlyList<(string Label, string? Value)> UserAgentChoices { get; } = [
		(Loc.S("Settings_UaDefault", "aria2 default"), null),
		("Chrome", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"),
		("Firefox", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0"),
		("Edge", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0"),
		(Loc.S("Settings_UaCustom", "Custom"), "")];

	/// <summary>A fresh read of the settings file; the Settings page fills its controls from this.</summary>
	public AppSettings Current => settings.Load();

	/// <summary>Saves a change and pushes it to the running engine right away.</summary>
	public void Update(Action<AppSettings> apply) {
		Change(apply);
		var saved = settings.Load();
		fileLog.MinLevel = EngineOptions.AppLogLevelFor(saved.LogLevel);
		_ = applier.ApplyAsync(saved);
	}

	/// <summary>The settings as JSON for export (machine-specific values left out).</summary>
	public string ExportJson() => SettingsTransfer.Export(settings.Load());

	/// <summary>Replaces the settings with an exported file's. Returns an error text, or null when imported and applied.</summary>
	public string? ImportJson(string json) {
		var (imported, error) = SettingsTransfer.Import(json, settings.Load());
		if (imported is null) {
			return error;
		}
		return Replace(imported);
	}

	/// <summary>Resets every preference to its default (the window position, last folder and similar machine state stay).</summary>
	public string? ResetAll() => Replace(SettingsTransfer.ResetAll(settings.Load()));

	private string? Replace(AppSettings next) {
		try {
			settings.Save(next);
		} catch (IOException ex) {
			logger.LogWarning(ex, "Could not save settings");
			return Loc.F("Settings_SaveFailed", "Could not save the settings: {0}", ex.Message);
		}
		fileLog.MinLevel = EngineOptions.AppLogLevelFor(next.LogLevel);
		App.ApplyTheme(next.Theme);
		_ = applier.ApplyAsync(next);
		return null;
	}

	/// <summary>The hand-typed options as text, one name=value per line.</summary>
	public string RawOptionsText => RawOptionRules.ToText(settings.Load().RawOptions);

	/// <summary>Saves hand-typed options. Returns the problem (nothing is saved) or null.</summary>
	public string? SetRawOptions(string text) {
		var (options, error) = RawOptionRules.Parse(text);
		if (error is not null) {
			return error;
		}
		Update(s => s.RawOptions = options);
		return null;
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
			s.Conflict = d.Conflict;
		});
	}

	public void ResetSpeed() {
		Update(s => {
			s.DownloadLimitKiBps = s.UploadLimitKiBps = s.AltDownloadLimitKiBps = s.AltUploadLimitKiBps = 0;
			s.AltSpeedActive = false;
		});
	}

	public void ResetTorrents() {
		var d = new AppSettings();
		Update(s => {
			s.ListenPortStart = d.ListenPortStart;
			s.ListenPortEnd = d.ListenPortEnd;
			s.DhtPort = d.DhtPort;
			s.EnableDht = d.EnableDht;
			s.EnablePex = d.EnablePex;
			s.EnableLpd = d.EnableLpd;
			s.Encryption = d.Encryption;
			s.SeedRatio = d.SeedRatio;
			s.SeedTimeMinutes = d.SeedTimeMinutes;
			s.Trackers = d.Trackers;
			s.TrackerUrl = d.TrackerUrl;
		});
	}

	public void ResetNetwork() {
		var d = new AppSettings();
		Update(s => {
			s.Proxy = d.Proxy;
			s.ProxyAddress = d.ProxyAddress;
			s.ProxyUser = d.ProxyUser;
			s.ProxyPasswordProtected = d.ProxyPasswordProtected;
			s.CheckCertificates = d.CheckCertificates;
			s.UseIPv6 = d.UseIPv6;
		});
	}

	/// <summary>Saves the proxy password protected; an empty password clears it.</summary>
	public void SetProxyPassword(string password) => Update(s => s.ProxyPasswordProtected = SecretProtector.Protect(password));

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
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			return Loc.F("Settings_FolderNotWritable", "Cannot write to this folder: {0}", ex.Message);
		}
	}

	/// <summary>Free space on the drive holding <paramref name="path"/>, as text, or empty if unknown.</summary>
	public static string FreeSpaceText(string path) {
		try {
			var root = Path.GetPathRoot(Path.GetFullPath(path));
			return root is null ? "" : Loc.F("Settings_FreeSpace", "{0} free", FormatBytes(new DriveInfo(root).AvailableFreeSpace));
		} catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) {
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
	public static IReadOnlyList<(string Label, int Days)> RetentionChoices { get; } = [(Loc.S("Settings_KeepForever", "Keep forever"), 0), (Loc.S("Settings_Days30", "30 days"), 30), (Loc.S("Settings_Days90", "90 days"), 90), (Loc.S("Settings_Year1", "1 year"), 365)];

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
			} catch (IOException ex) {
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
			} catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) {
				return false;
			}
		}
		set {
			try {
				if (value) {
					startup.Enable();
				} else {
					startup.Disable();
				}
			} catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
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

	public bool AutoCheckUpdates {
		get => settings.Load().AutoCheckUpdates;
		set => Change(s => s.AutoCheckUpdates = value);
	}

	public bool IncludePreReleases {
		get => settings.Load().IncludePreReleases;
		set => Change(s => s.IncludePreReleases = value);
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
		} catch (IOException ex) {
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
			} catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) {
				logger.LogWarning(ex, "Could not save the history setting");
			}
		}
	}

	public int ClearHistory() => history.Store?.Clear() ?? 0;
}
