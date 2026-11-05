using Aria2NET.Core;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Aria2NET.ViewModels;

public sealed class SettingsViewModel(AppPaths paths, SettingsStore<AppSettings> settings, HistoryService history, ILogger<SettingsViewModel> logger) {
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
