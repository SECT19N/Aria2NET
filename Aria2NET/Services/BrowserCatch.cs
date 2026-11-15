using System.Diagnostics;
using Aria2NET.Core;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Aria2NET.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Aria2NET.Services;

/// <summary>
/// Catching downloads from Chrome and Edge. While it is switched on the browsers know this exe as the extension's native messaging host,
/// and the app listens on a pipe for what that host process passes on. Must be created on the UI thread.
/// </summary>
public sealed class BrowserCatch(SettingsStore<AppSettings> settings, AppPaths paths, DownloadsViewModel downloads, ILoggerFactory loggers) {
	private readonly ILogger _logger = loggers.CreateLogger<BrowserCatch>();
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
	private readonly NativeMessagingRegistration? _registration =
		Environment.ProcessPath is { Length: > 0 } exe ? new NativeMessagingRegistration(exe, ManifestDir(paths)) : null;
	private BrowserBridgeServer? _server;

	/// <summary>Where the host manifest lives: with the app's data, which survives updates (the install folder is replaced by them).</summary>
	public static string ManifestDir(AppPaths paths) => Path.Combine(paths.DataRoot, "browser");

	/// <summary>The unpacked extension shipped next to the exe, for "Load unpacked" in the browser.</summary>
	public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "browser-extension");

	public bool IsAvailable => _registration is not null;

	public bool IsEnabled => settings.Load().CatchBrowserDownloads;

	/// <summary>At launch: when catching is on, repair the registration (the exe may have moved) and start listening.</summary>
	public void Start() {
		if (IsEnabled) {
			Apply(true);
		}
	}

	/// <summary>Switches catching on or off. Returns an error text when the registration could not be written.</summary>
	public string? SetEnabled(bool enabled) {
		string? problem = Apply(enabled);
		try {
			AppSettings s = settings.Load();
			s.CatchBrowserDownloads = enabled && problem is null;
			settings.Save(s);
		} catch (IOException ex) {
			_logger.LogWarning(ex, "Could not save settings");
		}
		return problem;
	}

	private string? Apply(bool enabled) {
		try {
			if (enabled) {
				_registration?.Register();
				_server ??= new BrowserBridgeServer(HandleAsync, _logger);
				_server.Start();
				_logger.LogInformation("Catching browser downloads (host manifest {Path})", _registration?.ManifestPath);
			} else {
				_registration?.Unregister();
				if (_server is { } server) {
					_ = server.StopAsync();
				}
				_logger.LogInformation("Stopped catching browser downloads");
			}
			return null;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
			_logger.LogWarning(ex, "Could not change the browser registration");
			return ex.Message;
		}
	}

	/// <summary>Runs on a pool thread; the add itself has to happen on the UI thread. Returns null when the download was taken, else why not.</summary>
	private Task<string?> HandleAsync(BrowserMessage message) {
		if (message.Download is not { } request) {
			return Task.FromResult<string?>(null); // a ping: being here is the answer
		}
		TaskCompletionSource<string?> done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_dispatcher.TryEnqueue(async () => {
			try {
				done.TrySetResult(await downloads.AddFromBrowserAsync(request));
			} catch (Exception ex) {
				_logger.LogWarning(ex, "Adding a browser download failed");
				done.TrySetResult(ex.Message);
			}
		})) {
			done.TrySetResult("Aria2NET is shutting down.");
		}
		return done.Task;
	}
}

/// <summary>
/// The other life of Aria2NET.exe: started by the browser as the extension's native messaging host. It reads one message from stdin, passes
/// it to the running app (starting it in the tray when it is not running), writes the answer to stdout and exits. No window, no engine.
/// </summary>
internal static class BrowserHost {
	private static readonly TimeSpan AppStartTimeout = TimeSpan.FromSeconds(30);

	public static int Run(string[] args) {
		if (!BrowserBridge.IsAllowedHostLaunch(args)) {
			return 2;
		}
		try {
			return RunAsync().GetAwaiter().GetResult();
		} catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) {
			return 1;
		}
	}

	private static async Task<int> RunAsync() {
		using Stream input = Console.OpenStandardInput();
		using Stream output = Console.OpenStandardOutput();
		if (await BrowserBridge.ReadFrameAsync(input) is not { } json) {
			return 0;
		}
		(BrowserMessage? message, string? error) = BrowserBridge.Parse(json);
		string reply;
		if (message is null) {
			reply = BrowserBridge.Reply(false, error);
		} else if (message.IsPing) {
			// Answered here so that opening the extension's options does not start the app.
			reply = BrowserBridge.Reply(true);
		} else {
			reply = await ForwardAsync(json) ?? BrowserBridge.Reply(false, "Aria2NET is not running, or catching browser downloads is switched off in its settings.");
		}
		await BrowserBridge.WriteFrameAsync(output, reply);
		return 0;
	}

	private static async Task<string?> ForwardAsync(string json) {
		if (await BrowserBridge.SendToAppAsync(json, TimeSpan.FromMilliseconds(500)) is { } reply) {
			return reply;
		}
		// The running app holds this event (see InstanceTakeover). Running but not listening means catching is switched off.
		if (EventWaitHandle.TryOpenExisting(InstanceRules.ReplaceEventName, out EventWaitHandle? running)) {
			running.Dispose();
			return null;
		}
		if (Environment.ProcessPath is not { Length: > 0 } exe) {
			return null;
		}
		// Through the shell, so the app does not inherit this process's pipes to the browser.
		using (Process.Start(new ProcessStartInfo(exe, LaunchArguments.BackgroundArgument) { UseShellExecute = true })) {
		}
		DateTime deadline = DateTime.UtcNow + AppStartTimeout;
		while (DateTime.UtcNow < deadline) {
			if (await BrowserBridge.SendToAppAsync(json, TimeSpan.FromSeconds(1)) is { } late) {
				return late;
			}
			await Task.Delay(300);
		}
		return null;
	}
}
