using System.Diagnostics;
using System.Runtime.InteropServices;
using Aria2NET.Core.Background;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Services;

/// <summary>"When downloads finish: sleep / hibernate / shut down / exit". Chosen from the tray menu or Settings, valid for this run only.</summary>
internal sealed class WhenFinishedService(DownloadMonitor monitor, ILoggerFactory loggerFactory) {
	private const int CountdownSeconds = 30;

	private readonly ILogger _logger = loggerFactory.CreateLogger<WhenFinishedService>();
	private readonly WhenFinishedMachine _machine = new();
	private readonly object _gate = new(); // the monitor thread observes while the UI thread arms
	private MainWindow? _window;

	public FinishAction Action {
		get {
			lock (_gate) {
				return _machine.Action;
			}
		}
		set {
			lock (_gate) {
				_machine.Arm(value);
			}
			_logger.LogInformation("When downloads finish: {Action}", value);
		}
	}

	public void Start(MainWindow window) {
		_window = window;
		monitor.SnapshotReady += (_, snapshot) => {
			FinishAction fire;
			lock (_gate) {
				fire = _machine.Observe(snapshot);
			}
			if (fire != FinishAction.None) {
				window.DispatcherQueue.TryEnqueue(() => _ = RunAsync(fire));
			}
		};
	}

	private async Task RunAsync(FinishAction action) {
		try {
			if (action == FinishAction.Exit) {
				_window!.RequestExit(confirm: false);
				return;
			}
			if (await CountdownAsync(_window!, action)) {
				Execute(action);
			}
		} catch (Exception ex) {
			_logger.LogError(ex, "Carrying out '{Action}' failed", action);
		}
	}

	/// <summary>Shows a cancellable countdown. Returns true when it ran out or the user chose to go ahead; false when cancelled (or no dialog could be shown, in which case nothing is done).</summary>
	private static async Task<bool> CountdownAsync(MainWindow window, FinishAction action) {
		window.BringToFront();
		var verb = action switch {
			FinishAction.Sleep => Loc.S("Finish_VerbSleep", "go to sleep"),
			FinishAction.Hibernate => Loc.S("Finish_VerbHibernate", "hibernate"),
			_ => Loc.S("Finish_VerbShutDown", "shut down"),
		};
		var remaining = CountdownSeconds;
		var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
		void Refresh() => text.Text = Loc.F("Finish_Countdown", "All downloads are finished. The computer will {0} in {1} seconds.", verb, remaining);
		Refresh();

		var dialog = new ContentDialog {
			Title = Loc.S("Finish_Title", "Downloads finished"),
			Content = text,
			PrimaryButtonText = Loc.F("Finish_Now", "{0} now", WhenFinishedMachine.Label(action)),
			CloseButtonText = Loc.S("Common_Cancel", "Cancel"),
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = window.Content.XamlRoot,
		};
		var expired = false;
		var timer = window.DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromSeconds(1);
		timer.Tick += (_, _) => {
			if (--remaining <= 0) {
				expired = true;
				timer.Stop();
				dialog.Hide();
			} else {
				Refresh();
			}
		};
		timer.Start();
		try {
			var result = await dialog.ShowAsync();
			return expired || result == ContentDialogResult.Primary;
		} catch (InvalidOperationException) {
			return false; // another dialog is open; never power off without showing the warning
		} finally {
			timer.Stop();
		}
	}

	private void Execute(FinishAction action) {
		switch (action) {
			case FinishAction.Sleep or FinishAction.Hibernate:
				if (!SetSuspendState(action == FinishAction.Hibernate, false, false)) {
					_logger.LogWarning("Windows refused to {Action}", action);
				}
				break;
			case FinishAction.Shutdown:
				Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
				break;
		}
	}

	[DllImport("powrprof.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
