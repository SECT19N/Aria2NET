using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Aria2NET.Pages;
using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace Aria2NET;

public sealed partial class MainWindow : Window {
	private readonly IAria2Engine _engine = App.Services.GetRequiredService<IAria2Engine>();
	private readonly EngineSupervisor _supervisor = App.Services.GetRequiredService<EngineSupervisor>();
	private readonly DownloadMonitor _monitor = App.Services.GetRequiredService<DownloadMonitor>();
	private readonly SettingsStore<AppSettings> _settings = App.Services.GetRequiredService<SettingsStore<AppSettings>>();
	private readonly ILogger<MainWindow> _logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
	private readonly DownloadsViewModel _downloads;
	private WindowPlacement? _lastRestored;
	private WindowPlacement? _savedFit;
	private readonly BackgroundCoordinator _background = App.Services.GetRequiredService<BackgroundCoordinator>();
	private readonly ToastService _toasts = App.Services.GetRequiredService<ToastService>();
	private volatile bool _foreground;
	private bool _closeApproved;
	private bool _exitRequested;
	private bool _skipExitConfirm;

	public ShellViewModel ViewModel { get; } = App.Services.GetRequiredService<ShellViewModel>();
	public NoticeCenter Notices { get; } = App.Services.GetRequiredService<NoticeCenter>();

	public MainWindow() {
		InitializeComponent();

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		AppWindow.Closing += OnClosing;
		RestorePlacement();
		AppWindow.Changed += OnAppWindowChanged;
		Activated += OnFirstActivated;
		Activated += (_, args) => _foreground = args.WindowActivationState != WindowActivationState.Deactivated;

		Nav.SelectedItem = Nav.MenuItems[0];

		// Resolved here so the view model is created on the UI thread and is subscribed before the first poll.
		_downloads = App.Services.GetRequiredService<DownloadsViewModel>();
		_monitor.Start();
	}

	/// <summary>True while this is the active window. Readable from any thread.</summary>
	public bool IsForeground => _foreground;

	/// <summary>Switches to the Downloads page.</summary>
	public void ShowDownloads() => Nav.SelectedItem = Nav.MenuItems[0];

	/// <summary>Shuts the app down for real (the close button may only hide it to the tray). Asks first when downloads are running, unless told not to.</summary>
	public void RequestExit(bool confirm = true) {
		_exitRequested = true;
		_skipExitConfirm = !confirm;
		Close();
	}

	/// <summary>Shows the window again if it was hidden to the tray or minimized, and brings it forward.</summary>
	public void BringToFront() {
		if (!AppWindow.IsVisible) {
			AppWindow.Show();
		}
		if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) {
			presenter.Restore();
		}
		Activate();
		SetForegroundWindow(WindowNative.GetWindowHandle(this));
	}

	public async Task ShowErrorAsync(string title, string message) {
		BringToFrontIfHidden(); // a dialog on a hidden window would wait there unseen
		var dialog = new ContentDialog {
			Title = title,
			Content = message + "\n\nDetails were written to the log file.",
			CloseButtonText = "OK",
			XamlRoot = Content.XamlRoot,
		};
		await dialog.ShowAsync();
	}

	/// <summary>Asks a yes/no question. Returns false when the user cancels or another dialog is already open.</summary>
	public async Task<bool> ConfirmAsync(string title, string message, string confirmText) {
		BringToFrontIfHidden();
		var dialog = new ContentDialog {
			Title = title,
			Content = message,
			PrimaryButtonText = confirmText,
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = Content.XamlRoot,
		};
		try {
			return await dialog.ShowAsync() == ContentDialogResult.Primary;
		}
		catch (InvalidOperationException) {
			return false; // WinUI allows one open dialog at a time
		}
	}

	private void BringToFrontIfHidden() {
		if (!AppWindow.IsVisible) {
			BringToFront();
		}
	}

	private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) {
		var page = args.IsSettingsSelected
			? typeof(SettingsPage)
			: (args.SelectedItemContainer?.Tag as string) switch {
				"downloads" => typeof(DownloadsPage),
				"history" => typeof(HistoryPage),
				"about" => typeof(AboutPage),
				_ => null,
			};
		if (page is not null && ContentFrame.CurrentSourcePageType != page) {
			ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
		}
	}

	// ---------------------------------------------------------------- messages

	private void NoticeBar_Close(InfoBar sender, object args) => Notices.Dismiss();

	private async void NoticePrimary_Click(object sender, RoutedEventArgs e) => await RunNoticeAsync(Notices.Current?.Primary);

	private async void NoticeSecondary_Click(object sender, RoutedEventArgs e) => await RunNoticeAsync(Notices.Current?.Secondary);

	private async Task RunNoticeAsync(NoticeAction? action) {
		if (action is null) {
			return;
		}
		Notices.Dismiss();
		try {
			await action.Run();
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Notice action {Action} failed", action.Text);
		}
	}

	// ---------------------------------------------------------------- window position

	private void RestorePlacement() {
		try {
			_savedFit = null;
			// Indexed on purpose: LINQ over this WinRT list fails to marshal (InvalidCastException).
			var areas = DisplayArea.FindAll();
			var displays = new List<ScreenRect>();
			for (var i = 0; i < areas.Count; i++) {
				var rect = areas[i].WorkArea;
				var screen = new ScreenRect(rect.X, rect.Y, rect.Width, rect.Height);
				if (areas[i].IsPrimary) {
					displays.Insert(0, screen);
				}
				else {
					displays.Add(screen);
				}
			}
			if (WindowPlacementFitter.Fit(_settings.Load().Window, displays) is { } fit) {
				_savedFit = fit;
				ApplyPlacement(fit);
			}
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Could not restore the window position");
		}
		_lastRestored = CurrentPlacement();
	}

	private void ApplyPlacement(WindowPlacement fit) {
		AppWindow.MoveAndResize(new RectInt32(fit.X, fit.Y, fit.Width, fit.Height));
		if (fit.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) {
			presenter.Maximize();
		}
	}

	// Windows picks its own spot when the window is first shown and ignores the move made in the constructor, so do it once more.
	private void OnFirstActivated(object sender, WindowActivatedEventArgs args) {
		Activated -= OnFirstActivated;
		if (_savedFit is { } fit) {
			try {
				ApplyPlacement(fit);
			}
			catch (Exception ex) {
				_logger.LogWarning(ex, "Could not restore the window position");
			}
		}
	}

	private WindowPlacement CurrentPlacement() => new() {
		X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height,
	};

	// While maximized or minimized the window reports the wrong rectangle, so only remember the normal one.
	private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) {
		if ((args.DidPositionChange || args.DidSizeChange) && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored }) {
			_lastRestored = CurrentPlacement();
		}
		// Minimizing is reported as a move or resize; hide the window so only the tray icon is left.
		if ((args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
			&& sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
			&& _background.TrayAvailable && _settings.Load().MinimizeToTray) {
			sender.Hide();
		}
	}

	private void SavePlacement() {
		try {
			var placement = _lastRestored ?? CurrentPlacement();
			placement.Maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
			var settings = _settings.Load();
			settings.Window = placement;
			_settings.Save(settings);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			_logger.LogWarning(ex, "Could not save the window position");
		}
	}

	// ---------------------------------------------------------------- closing

	// Stop aria2 gracefully (it saves its session) before the window goes away.
	private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args) {
		if (_closeApproved) {
			return;
		}
		args.Cancel = true;
		var settings = _settings.Load();
		// Only hide when the tray icon is really there; otherwise the user would have no way back.
		if (!_exitRequested && settings.CloseToTray && _background.TrayAvailable) {
			SavePlacement();
			AppWindow.Hide();
			return;
		}
		if (settings.ConfirmExitWithActive && !_skipExitConfirm && _background.ActiveDownloads is > 0 and var running) {
			BringToFront();
			var message = running == 1
				? "A download is still running. Exiting stops it; it continues the next time you open Aria2NET."
				: $"{running} downloads are still running. Exiting stops them; they continue the next time you open Aria2NET.";
			if (!await ConfirmAsync("Exit Aria2NET?", message, "Exit")) {
				_exitRequested = false;
				return;
			}
		}
		SavePlacement();
		try {
			await _downloads.FinalizePendingAsync(); // a removal still inside its undo window becomes final
			_supervisor.Dispose();                   // stopping on purpose must not look like a crash to restart
			await _monitor.StopAsync();
			await _engine.StopAsync();
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Engine stop threw during shutdown");
		}
		_toasts.Dispose();
		_background.Dispose(); // takes the icon out of the notification area so no ghost is left behind
		_closeApproved = true;
		Close();
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);
}
