using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Logging;
using Aria2NET.Core.Settings;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Aria2NET;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application {
	private static MainWindow? _window;
	private bool _showingError;

	public static IServiceProvider Services { get; private set; } = null!;

	/// <summary>The main window (needed for window handles, e.g. folder pickers).</summary>
	public static MainWindow Window => _window ?? throw new InvalidOperationException("Main window not created yet");

	public App() {
		InitializeComponent();
		Services = ConfigureServices();
		UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += (_, e) => {
			Log.LogError(e.Exception, "Unobserved task exception");
			e.SetObserved();
		};
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			Log.LogCritical(e.ExceptionObject as Exception, "Unhandled domain exception (terminating: {Terminating})", e.IsTerminating);
	}

	private static ILogger Log => Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");

	private static ServiceProvider ConfigureServices() {
		var services = new ServiceCollection();
		var paths = AppPaths.CreateDefault();
		paths.EnsureDirectories();

		services.AddSingleton(paths);
		services.AddLogging(b => b.ClearProviders().AddProvider(new FileLoggerProvider(paths.LogsDir)));
		services.AddSingleton(sp => new SettingsStore<AppSettings>(
			paths.SettingsFile, AppSettingsJsonContext.Default.AppSettings, sp.GetRequiredService<ILogger<SettingsStore<AppSettings>>>()));
		services.AddSingleton<IAria2Engine>(sp => new Aria2Engine(
			paths,
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
			sp.GetRequiredService<ILogger<Aria2Engine>>()));

		services.AddSingleton<DownloadMonitor>(sp => new DownloadMonitor(
			sp.GetRequiredService<IAria2Engine>(), sp.GetRequiredService<ILogger<DownloadMonitor>>()));
		services.AddSingleton<DownloadOperations>(sp => new DownloadOperations(
			sp.GetRequiredService<IAria2Engine>(), sp.GetRequiredService<ILogger<DownloadOperations>>()));
		services.AddSingleton<ShellViewModel>();
		services.AddSingleton<DownloadsViewModel>();
		services.AddTransient<AboutViewModel>();
		services.AddTransient<SettingsViewModel>();
		return services.BuildServiceProvider();
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args) {
		Log.LogInformation("Aria2NET {Version} starting", AboutViewModel.AppVersion);
		_window = new MainWindow();
		_window.Activate();
		_ = StartEngineAsync();
	}

	private static async Task StartEngineAsync() {
		try {
			await Services.GetRequiredService<IAria2Engine>().StartAsync();
		}
		catch (Exception ex) {
			Log.LogError(ex, "Engine start threw");
		}
	}

	/// <summary>Called when a second launch is redirected to this instance.</summary>
	public static void ActivateMainWindow() => _window?.DispatcherQueue.TryEnqueue(_window.BringToFront);

	private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) {
		Log.LogCritical(e.Exception, "Unhandled UI exception: {Message}", e.Message);
		e.Handled = true;
		if (_showingError || _window is null) {
			return;
		}
		_showingError = true;
		try {
			await _window.ShowErrorAsync("Something went wrong", e.Exception?.Message ?? e.Message);
		}
		catch (Exception ex) {
			Log.LogError(ex, "Could not show error dialog");
		}
		finally {
			_showingError = false;
		}
	}
}
