using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Aria2NET.Core.Tray;
using Microsoft.Extensions.Logging;
using WinRT.Interop;

namespace Aria2NET.Services;

/// <summary>Everything the app does while its window is hidden. Owns the tray icon and keeps it in step with the downloads.</summary>
internal sealed class BackgroundCoordinator(
	IAria2Engine engine,
	DownloadMonitor monitor,
	DownloadOperations operations,
	SettingsStore<AppSettings> settings,
	ILoggerFactory loggerFactory) : IDisposable {

	private readonly ILogger _logger = loggerFactory.CreateLogger<BackgroundCoordinator>();
	private TrayIcon? _tray;
	private volatile PollSnapshot? _latest;

	/// <summary>True while the tray icon is on screen, which is the condition for hiding the window instead of closing it.</summary>
	public bool TrayAvailable => _tray?.IsVisible == true;

	/// <summary>Downloads (including seeding torrents) that exiting now would stop.</summary>
	public int ActiveDownloads => engine.State == EngineState.Running ? _latest?.Stats.NumActive ?? 0 : 0;

	/// <summary>Must be called on the UI thread once the main window exists.</summary>
	public void Start(MainWindow window) {
		var dispatcher = window.DispatcherQueue;
		try {
			_tray = new TrayIcon(WindowNative.GetWindowHandle(window), Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), _logger);
			_tray.OpenRequested += window.BringToFront;
			_tray.PauseAllRequested += () => _ = RunAsync(operations.PauseAllAsync, "Pause all");
			_tray.ResumeAllRequested += () => _ = RunAsync(operations.ResumeAllAsync, "Resume all");
			_tray.OpenFolderRequested += OpenDownloadFolder;
			_tray.ExitRequested += () => window.RequestExit();
			_tray.Show();
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Could not create the tray icon; the window will close normally");
			_tray?.Dispose();
			_tray = null;
			return;
		}
		monitor.SnapshotReady += (_, snapshot) => {
			_latest = snapshot;
			dispatcher.TryEnqueue(Refresh);
		};
		engine.StateChanged += (_, _) => dispatcher.TryEnqueue(Refresh);
		Refresh();
	}

	private void Refresh() {
		var status = TrayStatus.From(_latest, engine.State);
		_tray?.Update(status.State, status.Tooltip);
	}

	private async Task RunAsync(Func<CancellationToken, Task> action, string name) {
		try {
			await action(CancellationToken.None);
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "{Action} from the tray failed", name);
		}
	}

	private void OpenDownloadFolder() {
		var s = settings.Load();
		var folder = new[] { s.LastUsedFolder, s.DefaultDownloadFolder }.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		if (!ShellLauncher.TryOpenFolder(folder, null, out var error)) {
			_logger.LogWarning("Could not open {Folder}: {Error}", folder, error);
		}
	}

	public void Dispose() {
		_tray?.Dispose();
		_tray = null;
	}
}
