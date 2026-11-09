using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Notifications;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Aria2NET.Services;

/// <summary>Windows toast notifications for finished, failed and finished-torrent downloads, with Open / Show in folder / Retry buttons.</summary>
internal sealed class ToastService(
	IAria2Engine engine,
	DownloadMonitor monitor,
	DownloadOperations operations,
	SettingsStore<AppSettings> settings,
	ILoggerFactory loggerFactory) : IDisposable {

	private readonly ILogger _logger = loggerFactory.CreateLogger<ToastService>();
	private readonly NotificationDetector _detector = new();
	private readonly NotificationBatcher _batcher = new(TimeSpan.FromSeconds(3));
	private readonly object _gate = new(); // the monitor thread polls while the engine thread resets
	private volatile PollSnapshot? _latest;
	private MainWindow? _window;
	private bool _registered;

	/// <summary>Call on the UI thread once the main window exists. Without toasts the app still works, so failures are only logged.</summary>
	public void Start(MainWindow window) {
		_window = window;
		try {
			AppNotificationManager.Default.NotificationInvoked += OnInvoked;
			AppNotificationManager.Default.Register();
			_registered = true;
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Could not register for toast notifications");
			return;
		}
		engine.StateChanged += (_, _) => {
			if (engine.State != EngineState.Running) {
				lock (_gate) {
					_detector.Reset(); // whatever is listed after a restart is a new baseline
				}
			}
		};
		monitor.SnapshotReady += (_, snapshot) => OnSnapshot(snapshot);
	}

	private void OnSnapshot(PollSnapshot snapshot) {
		_latest = snapshot;
		IReadOnlyList<ToastContent> due;
		lock (_gate) {
			var found = _detector.Observe(snapshot);
			if (found.Count > 0) {
				_batcher.Add(Allowed(found), DateTimeOffset.UtcNow);
			}
			due = _batcher.TakeDue(DateTimeOffset.UtcNow);
		}
		if (due.Count == 0) {
			return;
		}
		var sound = settings.Load().NotifySound;
		foreach (var toast in due) {
			try {
				AppNotificationManager.Default.Show(Build(toast, sound));
			}
			catch (Exception ex) {
				_logger.LogWarning(ex, "Could not show a toast");
			}
		}
	}

	/// <summary>Applies the per-event switches and the "only when the window is not in front" filter.</summary>
	private List<DownloadNotification> Allowed(IReadOnlyList<DownloadNotification> found) {
		var s = settings.Load();
		if (s.NotifyOnlyWhenUnfocused && _window?.IsForeground == true) {
			return [];
		}
		return found.Where(n => n.Kind switch {
			NotificationKind.Completed => s.NotifyOnComplete,
			NotificationKind.Failed => s.NotifyOnFailed,
			_ => s.NotifyOnTorrentFinished,
		}).ToList();
	}

	private static AppNotification Build(ToastContent toast, bool sound) {
		var builder = new AppNotificationBuilder().AddText(toast.Title).AddText(toast.Body);
		if (!sound) {
			builder.MuteAudio();
		}
		if (toast.Single is not { } item) {
			builder.AddArgument("action", "show");
		}
		else if (toast.Kind == NotificationKind.Failed) {
			builder.AddArgument("action", "show");
			if (DownloadCapabilities.CanRetry(item)) {
				builder.AddButton(new AppNotificationButton("Retry").AddArgument("action", "retry").AddArgument("gid", item.Gid));
			}
		}
		else {
			var path = item.Path ?? "";
			var dir = item.Dir ?? "";
			builder.AddArgument("action", "folder").AddArgument("path", path).AddArgument("dir", dir);
			if (path.Length > 0) {
				builder.AddButton(new AppNotificationButton("Open").AddArgument("action", "open").AddArgument("path", path));
			}
			builder.AddButton(new AppNotificationButton("Show in folder").AddArgument("action", "folder").AddArgument("path", path).AddArgument("dir", dir));
		}
		return builder.BuildNotification();
	}

	// Runs on a background thread when the user clicks a toast or one of its buttons.
	private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) {
		try {
			var values = args.Arguments;
			values.TryGetValue("action", out var action);
			values.TryGetValue("path", out var path);
			values.TryGetValue("dir", out var dir);
			switch (action) {
				case "open" when !string.IsNullOrEmpty(path):
					if (!ShellLauncher.TryOpenFile(path, out var openError)) {
						_logger.LogWarning("Could not open {Path}: {Error}", path, openError);
					}
					break;
				case "folder":
					var folder = !string.IsNullOrEmpty(dir) ? dir : Path.GetDirectoryName(path);
					if (!string.IsNullOrEmpty(folder) && !ShellLauncher.TryOpenFolder(folder, path, out var folderError)) {
						_logger.LogWarning("Could not open {Folder}: {Error}", folder, folderError);
					}
					break;
				case "retry" when values.TryGetValue("gid", out var gid):
					_ = RetryAsync(gid);
					break;
				default:
					_window?.DispatcherQueue.TryEnqueue(_window.BringToFront);
					break;
			}
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Handling a toast click failed");
		}
	}

	private async Task RetryAsync(string gid) {
		try {
			if (_latest?.Items.FirstOrDefault(i => i.Gid == gid) is { } item) {
				await operations.RetryAsync(item);
			}
			else {
				_window?.DispatcherQueue.TryEnqueue(_window.BringToFront); // the download is gone from the list; let the user look
			}
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Retry from a toast failed");
		}
	}

	public void Dispose() {
		if (!_registered) {
			return;
		}
		_registered = false;
		try {
			AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
			AppNotificationManager.Default.Unregister();
		}
		catch (Exception ex) {
			_logger.LogDebug(ex, "Unregistering from toast notifications failed");
		}
	}
}
