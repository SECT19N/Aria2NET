using Aria2NET.Core.Aria2;
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
	private readonly ILogger<ShellViewModel> _logger;
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

	[ObservableProperty]
	public partial string StatusText { get; private set; } = "Engine: starting…";

	[ObservableProperty]
	public partial Brush StatusBrush { get; private set; } = Busy;

	[ObservableProperty]
	public partial bool ShowError { get; private set; }

	[ObservableProperty]
	public partial string ErrorMessage { get; private set; } = "";

	public ShellViewModel(IAria2Engine engine, ILogger<ShellViewModel> logger) {
		_engine = engine;
		_logger = logger;
		_engine.StateChanged += (_, _) => _dispatcher.TryEnqueue(Refresh);
		Refresh();
	}

	[RelayCommand]
	private async Task RestartEngineAsync() {
		try {
			await _engine.RestartAsync();
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Engine restart threw");
		}
	}

	private void Refresh() {
		(StatusText, StatusBrush) = _engine.State switch {
			EngineState.Running => ($"aria2 {_engine.Version?.Version} · Running", Good),
			EngineState.Starting => ("Engine: starting…", Busy),
			EngineState.Restarting => ("Engine: restarting…", Busy),
			EngineState.Stopping => ("Engine: stopping…", Busy),
			EngineState.Stopped => ("Engine: stopped", Idle),
			EngineState.Crashed => ("Engine: crashed", Bad),
			_ => ("Engine: failed to start", Bad),
		};
		ShowError = _engine.State is EngineState.Failed or EngineState.Crashed;
		ErrorMessage = _engine.LastError ?? "";
	}
}
