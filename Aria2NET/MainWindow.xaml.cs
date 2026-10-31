using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Pages;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;

namespace Aria2NET;

public sealed partial class MainWindow : Window {
	private readonly IAria2Engine _engine = App.Services.GetRequiredService<IAria2Engine>();
	private readonly DownloadMonitor _monitor = App.Services.GetRequiredService<DownloadMonitor>();
	private readonly ILogger<MainWindow> _logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
	private readonly DownloadsViewModel _downloads;
	private bool _closeApproved;

	public ShellViewModel ViewModel { get; } = App.Services.GetRequiredService<ShellViewModel>();

	public MainWindow() {
		InitializeComponent();

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
		AppWindow.Closing += OnClosing;

		Nav.SelectedItem = Nav.MenuItems[0];

		// Resolved here so the view model is created on the UI thread and is subscribed before the first poll.
		_downloads = App.Services.GetRequiredService<DownloadsViewModel>();
		_monitor.Start();
	}

	public void BringToFront() {
		if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) {
			presenter.Restore();
		}
		Activate();
		SetForegroundWindow(WindowNative.GetWindowHandle(this));
	}

	public async Task ShowErrorAsync(string title, string message) {
		var dialog = new ContentDialog {
			Title = title,
			Content = message + "\n\nDetails were written to the log file.",
			CloseButtonText = "OK",
			XamlRoot = Content.XamlRoot,
		};
		await dialog.ShowAsync();
	}

	private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) {
		var page = args.IsSettingsSelected
			? typeof(SettingsPage)
			: (args.SelectedItemContainer?.Tag as string) switch {
				"downloads" => typeof(DownloadsPage),
				"about" => typeof(AboutPage),
				_ => null,
			};
		if (page is not null && ContentFrame.CurrentSourcePageType != page) {
			ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
		}
	}

	// Stop aria2 gracefully (it saves its session) before the window goes away.
	private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args) {
		if (_closeApproved) {
			return;
		}
		args.Cancel = true;
		try {
			await _downloads.FinalizePendingAsync(); // a removal still inside its undo window becomes final
			await _monitor.StopAsync();
			await _engine.StopAsync();
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Engine stop threw during shutdown");
		}
		_closeApproved = true;
		Close();
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);
}
