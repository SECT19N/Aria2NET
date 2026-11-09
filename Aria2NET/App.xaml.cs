using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Logging;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Aria2NET.Core.Torrents;
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

	/// <summary>Torrent files and magnet links given on the command line of the first launch.</summary>
	public static IReadOnlyList<string> StartupItems { get; set; } = [];

	/// <summary>Windows started this launch at sign-in (the Run key entry), not the user.</summary>
	public static bool StartedAtSignIn { get; set; }

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

		services.AddSingleton(sp => new TrackerListService(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, paths, sp.GetRequiredService<ILogger<TrackerListService>>()));
		services.AddSingleton(sp => new HistoryService(paths, sp.GetRequiredService<ILogger<HistoryService>>()));
		services.AddSingleton(sp => new EngineSupervisor(sp.GetRequiredService<IAria2Engine>(), new RestartPolicy(), sp.GetRequiredService<ILogger<EngineSupervisor>>()));
		services.AddSingleton<NoticeCenter>();
		services.AddSingleton<TorrentAssociation>();
		services.AddSingleton<PersistenceCoordinator>();
		services.AddSingleton<BackgroundCoordinator>();
		services.AddSingleton<ToastService>();
		services.AddSingleton<PowerService>();
		services.AddSingleton<WhenFinishedService>();
		services.AddSingleton(_ => new StartupRegistration(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Aria2NET.exe")));
		services.AddSingleton<DownloadMonitor>(sp => new DownloadMonitor(
			sp.GetRequiredService<IAria2Engine>(), sp.GetRequiredService<ILogger<DownloadMonitor>>()));
		services.AddSingleton<TorrentOperations>(sp => new TorrentOperations(
			sp.GetRequiredService<IAria2Engine>(), sp.GetRequiredService<ILogger<TorrentOperations>>()));
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
		Services.GetRequiredService<WhenFinishedService>().Start(_window);
		Services.GetRequiredService<PowerService>().Start(_window);
		var background = Services.GetRequiredService<BackgroundCoordinator>();
		background.Start(_window);
		Services.GetRequiredService<ToastService>().Start(_window);
		RefreshStartupEntry();
		// A sign-in start stays in the tray when asked to, but never without a tray icon: the window would then be unreachable.
		var hidden = StartedAtSignIn && background.TrayAvailable && Services.GetRequiredService<SettingsStore<AppSettings>>().Load().StartMinimized;
		if (!hidden) {
			_window.Activate();
		}
		if (StartupItems.Count > 0) {
			HandleExternalLaunch(StartupItems);
		}
		_ = StartEngineAsync();
	}

	/// <summary>If "start with Windows" is on and the app was moved, point the Run entry at the new location.</summary>
	private static void RefreshStartupEntry() {
		try {
			if (Services.GetRequiredService<StartupRegistration>().Refresh()) {
				Log.LogInformation("Updated the start-with-Windows entry to {Path}", Environment.ProcessPath);
			}
		}
		catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) {
			Log.LogWarning(ex, "Could not update the start-with-Windows entry");
		}
	}

	private static async Task StartEngineAsync() {
		try {
			var engine = Services.GetRequiredService<IAria2Engine>();
			await engine.StartAsync();
			if (engine.State == EngineState.Running && Services.GetRequiredService<SettingsStore<AppSettings>>().Load().StartPaused) {
				await Services.GetRequiredService<DownloadOperations>().PauseAllAsync();
				Log.LogInformation("Paused everything at start, as the settings ask");
			}
			_ = RefreshTrackerListAsync(engine);
		}
		catch (Exception ex) {
			Log.LogError(ex, "Engine start threw");
		}
	}

	/// <summary>Refreshes the cached public tracker list when it is a week old and hands it to the running engine. The engine reads the cache itself on every start, so failing here costs nothing.</summary>
	private static async Task RefreshTrackerListAsync(IAria2Engine engine) {
		try {
			var trackers = Services.GetRequiredService<TrackerListService>();
			if (!trackers.IsStale(DateTime.UtcNow)) {
				return;
			}
			if (await trackers.RefreshAsync() is { } list && engine.Rpc is { } rpc) {
				await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["bt-tracker"] = Aria2Config.TrackerOptionValue(list) });
			}
		}
		catch (Exception ex) {
			Log.LogWarning(ex, "Tracker list refresh failed");
		}
	}

	/// <summary>Shows the Add dialog filled with torrent files or magnet links that Windows handed to the app. Callable from any thread.</summary>
	public static void HandleExternalLaunch(IReadOnlyList<string> items) {
		if (items.Count == 0 || _window is not { } window) {
			return;
		}
		window.DispatcherQueue.TryEnqueue(() => {
			window.ShowDownloads();
			Services.GetRequiredService<DownloadsViewModel>().RequestExternalAdd(items);
		});
	}

	/// <summary>Shows the window with an empty Add dialog (the global hotkey). Callable from any thread.</summary>
	public static void ShowAddDialog() {
		if (_window is not { } window) {
			return;
		}
		window.DispatcherQueue.TryEnqueue(() => {
			window.BringToFront();
			window.ShowDownloads();
			Services.GetRequiredService<DownloadsViewModel>().RequestExternalAdd([]);
		});
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

	[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	internal static extern int RegisterApplicationRestart(string commandLine, uint flags);

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	internal static extern int UnregisterApplicationRestart();
}
