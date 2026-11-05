using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Logging;
using Aria2NET.Core.Settings;
using Aria2NET.Services;
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
	private static DataFolderLock? _dataLock; // held until the process ends

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

		_dataLock = DataFolderLock.TryAcquire(paths.LockFile);
		if (_dataLock is null) {
			// Another copy (or another Windows session of this user) is using this data folder; two writers would corrupt it.
			NativeMethods.MessageBox(IntPtr.Zero, "Aria2NET is already running (maybe in another Windows session) and is using the same data folder.", "Aria2NET", 0x10);
			Environment.Exit(1);
		}

		services.AddSingleton(paths);
		services.AddLogging(b => b.ClearProviders().AddProvider(new FileLoggerProvider(paths.LogsDir)));
		services.AddSingleton(sp => new SettingsStore<AppSettings>(
			paths.SettingsFile, AppSettingsJsonContext.Default.AppSettings, sp.GetRequiredService<ILogger<SettingsStore<AppSettings>>>()));
		services.AddSingleton<IAria2Engine>(sp => new Aria2Engine(
			paths,
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
			sp.GetRequiredService<ILogger<Aria2Engine>>()));

		services.AddSingleton(sp => new HistoryService(paths, sp.GetRequiredService<ILogger<HistoryService>>()));
		services.AddSingleton(sp => new EngineSupervisor(sp.GetRequiredService<IAria2Engine>(), new RestartPolicy(), sp.GetRequiredService<ILogger<EngineSupervisor>>()));
		services.AddSingleton<NoticeCenter>();
		services.AddSingleton<PersistenceCoordinator>();
		services.AddSingleton<DownloadMonitor>(sp => new DownloadMonitor(
			sp.GetRequiredService<IAria2Engine>(), sp.GetRequiredService<ILogger<DownloadMonitor>>()));
		services.AddSingleton<DownloadOperations>(sp => new DownloadOperations(
			sp.GetRequiredService<IAria2Engine>(), sp.GetRequiredService<ILogger<DownloadOperations>>()));
		services.AddSingleton<ShellViewModel>();
		services.AddSingleton<DownloadsViewModel>();
		services.AddTransient<AboutViewModel>();
		services.AddTransient<SettingsViewModel>();
		services.AddTransient<HistoryViewModel>();
		return services.BuildServiceProvider();
	}

	protected override void OnLaunched(LaunchActivatedEventArgs args) {
		Log.LogInformation("Aria2NET {Version} starting", AboutViewModel.AppVersion);
		_ = Services.GetRequiredService<NoticeCenter>(); // created on the UI thread so any thread can post to it
		_window = new MainWindow();
		Services.GetRequiredService<PersistenceCoordinator>().Start();
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

internal static class NativeMethods {
	[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	internal static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
