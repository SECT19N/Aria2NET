using Aria2NET.Core;
using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Aria2NET.ViewModels;

public sealed class SettingsViewModel(AppPaths paths, SettingsStore<AppSettings> settings, ILogger<SettingsViewModel> logger) {
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
}
