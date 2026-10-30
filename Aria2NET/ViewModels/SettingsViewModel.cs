using Aria2NET.Core;

namespace Aria2NET.ViewModels;

public sealed class SettingsViewModel(AppPaths paths) {
	public string DataFolder { get; } = paths.DataRoot;
	public string LogsFolder { get; } = paths.LogsDir;
}
