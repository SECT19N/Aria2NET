using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Aria2NET.ViewModels;

/// <summary>Engine status shown in the footer and the error banner. Must be created on the UI thread.</summary>
public sealed partial class ShellViewModel : ObservableObject {
	private static readonly Brush Good = new SolidColorBrush(Colors.SeaGreen);
	private static readonly Brush Busy = new SolidColorBrush(Colors.Orange);
	private static readonly Brush Bad = new SolidColorBrush(Colors.IndianRed);
	private static readonly Brush Idle = new SolidColorBrush(Colors.Gray);

	private readonly IAria2Engine _engine;
	private readonly EngineSupervisor _supervisor;
	private readonly ILogger<ShellViewModel> _logger;
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

	[ObservableProperty]
	public partial string StatusText { get; private set; } = Loc.S("Shell_Starting", "Engine: starting…");

	[ObservableProperty]
	public partial Brush StatusBrush { get; private set; } = Busy;

	/// <summary>The status bar segments. Hidden while the engine is down: stale speeds would mislead.</summary>
	[ObservableProperty]
	public partial bool ShowStats { get; private set; }

	[ObservableProperty]
	public partial string DownloadSpeedText { get; private set; } = "";

	[ObservableProperty]
	public partial string UploadSpeedText { get; private set; } = "";

	[ObservableProperty]
	public partial string ActiveText { get; private set; } = "";

	[ObservableProperty]
	public partial string WaitingText { get; private set; } = "";

	[ObservableProperty]
	public partial string SeedingText { get; private set; } = "";

	[ObservableProperty]
	public partial string CompletedText { get; private set; } = "";

	/// <summary>Connected torrent peers. aria2 does not report DHT nodes or whether the listen port is reachable.</summary>
	[ObservableProperty]
	public partial string PeersText { get; private set; } = "";

	[ObservableProperty]
	public partial bool ShowPeers { get; private set; }

	[ObservableProperty]
	public partial bool ShowError { get; private set; }

	[ObservableProperty]
	public partial string ErrorMessage { get; private set; } = "";

	public ShellViewModel(IAria2Engine engine, EngineSupervisor supervisor, DownloadMonitor monitor, ILogger<ShellViewModel> logger) {
		_engine = engine;
		_supervisor = supervisor;
		_logger = logger;
		_engine.StateChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
		_supervisor.GaveUp += (_, _) => _dispatcher.TryEnqueue(Refresh);
		monitor.SnapshotReady += (_, snapshot) => _dispatcher.TryEnqueue(() => ShowCounts(StatusCounts.From(snapshot)));
		ShowCounts(StatusCounts.Empty);
		Refresh();
	}

	[RelayCommand]
	private async Task RestartEngineAsync() {
		try {
			await _engine.RestartAsync();
		} catch (Exception ex) {
			_logger.LogError(ex, "Engine restart threw");
		}
	}

	private void Refresh() {
		bool recovering = _engine.State == EngineState.Crashed && _supervisor.IsRecovering;
		(StatusText, StatusBrush) = _engine.State switch {
			EngineState.Running => (Loc.F("Shell_Running", "aria2 {0} · Running", _engine.Version?.Version), Good),
			EngineState.Starting => (Loc.S("Shell_Starting", "Engine: starting…"), Busy),
			EngineState.Restarting => (Loc.S("Shell_Restarting", "Engine: restarting…"), Busy),
			EngineState.Stopping => (Loc.S("Shell_Stopping", "Engine: stopping…"), Busy),
			EngineState.Stopped => (Loc.S("Shell_Stopped", "Engine: stopped"), Idle),
			EngineState.Crashed => recovering ? (Loc.S("Shell_CrashRestarting", "Engine: restarting after a crash…"), Busy) : (Loc.S("Shell_Crashed", "Engine: crashed"), Bad),
			_ => (Loc.S("Shell_Failed", "Engine: failed to start"), Bad),
		};
		// A crash the supervisor is already dealing with is not worth an error banner; if it gives up, the banner appears.
		ShowError = _engine.State == EngineState.Failed || (_engine.State == EngineState.Crashed && !recovering);
		ErrorMessage = _engine.LastError ?? "";
		if (_engine.State != EngineState.Running) {
			ShowCounts(StatusCounts.Empty);
		}
	}

	private void ShowCounts(StatusCounts counts) {
		if (_engine.State != EngineState.Running) {
			counts = StatusCounts.Empty;
		}
		ShowStats = _engine.State == EngineState.Running;
		DownloadSpeedText = Loc.F("Bar_Down", "↓ {0}", ByteFormat.Speed(counts.DownloadSpeed));
		UploadSpeedText = Loc.F("Bar_Up", "↑ {0}", ByteFormat.Speed(counts.UploadSpeed));
		ActiveText = Loc.F("Bar_Active", "Active: {0}", counts.Active);
		WaitingText = Loc.F("Bar_Waiting", "Waiting: {0}", counts.Waiting);
		SeedingText = Loc.F("Bar_Seeding", "Seeding: {0}", counts.Seeding);
		CompletedText = Loc.F("Bar_Completed", "Completed: {0}", counts.Completed);
		PeersText = Loc.F("Bar_Peers", "Peers: {0}", counts.Peers);
		ShowPeers = counts.Peers > 0;
	}
}
