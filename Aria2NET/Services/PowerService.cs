using System.Runtime.InteropServices;
using Aria2NET.Core.Background;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;
using Windows.System.Power;
using WinRT.Interop;

namespace Aria2NET.Services;

/// <summary>What the machine does around the downloads: stays awake, shows overall progress on the taskbar, and pauses on battery or a metered connection.</summary>
internal sealed class PowerService(
	DownloadMonitor monitor,
	DownloadOperations operations,
	SettingsStore<AppSettings> settings,
	ILoggerFactory loggerFactory) {

	private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

	private readonly ILogger _logger = loggerFactory.CreateLogger<PowerService>();
	private readonly AutoPauseController _autoPause = new();
	private TaskbarProgressBar? _taskbar;
	private AppSettings _settings = new();
	private DateTime _settingsRead = DateTime.MinValue;
	private volatile bool _onBattery;
	private volatile bool _metered;
	private bool _blockingSleep;

	/// <summary>Call on the UI thread once the main window exists. SetThreadExecutionState belongs to the calling thread, so all work stays on it.</summary>
	public void Start(MainWindow window) {
		var dispatcher = window.DispatcherQueue;
		_taskbar = new TaskbarProgressBar(WindowNative.GetWindowHandle(window));
		WatchPower();
		monitor.SnapshotReady += (_, snapshot) => dispatcher.TryEnqueue(() => Update(snapshot));
	}

	private void Update(PollSnapshot snapshot) {
		// The settings file is read at most every few seconds, not on every poll.
		if (DateTime.UtcNow - _settingsRead > TimeSpan.FromSeconds(5)) {
			_settings = settings.Load();
			_settingsRead = DateTime.UtcNow;
		}

		var downloading = snapshot.Items.Any(i => i.Status == DownloadStatus.Active);
		var wantAwake = _settings.PreventSleep && downloading;
		if (wantAwake != _blockingSleep) {
			_blockingSleep = wantAwake;
			SetThreadExecutionState(EsContinuous | (wantAwake ? EsSystemRequired : 0));
		}

		_taskbar?.Apply(TaskbarProgress.From(snapshot));

		var shouldPause = (_settings.PauseOnBattery && _onBattery) || (_settings.PauseOnMetered && _metered);
		var decision = _autoPause.Evaluate(shouldPause, snapshot);
		if (decision.Action != AutoPauseAction.None) {
			_ = ApplyAsync(decision);
		}
	}

	private async Task ApplyAsync(AutoPauseDecision decision) {
		try {
			if (decision.Action == AutoPauseAction.Pause) {
				await operations.PauseAsync(decision.Items);
				_logger.LogInformation("Paused {Count} downloads (battery or metered connection)", decision.Items.Count);
			} else {
				await operations.ResumeAsync(decision.Items);
				_logger.LogInformation("Resumed {Count} downloads", decision.Items.Count);
			}
		} catch (Exception ex) {
			_logger.LogWarning(ex, "Automatic {Action} failed", decision.Action);
		}
	}

	private void WatchPower() {
		try {
			_onBattery = ReadOnBattery();
			PowerManager.PowerSupplyStatusChanged += (_, _) => _onBattery = ReadOnBattery();
			PowerManager.BatteryStatusChanged += (_, _) => _onBattery = ReadOnBattery();
		} catch (Exception ex) when (ex is COMException or InvalidOperationException) {
			_logger.LogInformation(ex, "Battery status is not available");
		}
		try {
			_metered = ReadMetered();
			NetworkInformation.NetworkStatusChanged += _ => _metered = ReadMetered();
		} catch (Exception ex) when (ex is COMException or InvalidOperationException) {
			_logger.LogInformation(ex, "Connection cost is not available");
		}
	}

	/// <summary>True on a laptop that is running from its battery (a desktop has no battery and is never "on battery").</summary>
	private static bool ReadOnBattery() =>
		PowerManager.BatteryStatus != BatteryStatus.NotPresent && PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent;

	private static bool ReadMetered() {
		try {
			var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
			return cost?.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
		} catch (Exception ex) when (ex is COMException or InvalidOperationException) {
			return false;
		}
	}

	[DllImport("kernel32.dll")]
	private static extern uint SetThreadExecutionState(uint flags);
}
