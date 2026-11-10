using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Settings;

/// <summary>Pushes settings to the running engine with <c>changeGlobalOption</c>, and owns the alternative-speed toggle.</summary>
public sealed class SettingsApplier(IAria2Engine engine, SettingsStore<AppSettings> store, DownloadOperations operations, ILogger<SettingsApplier> logger) {
	private readonly Lock _lock = new();
	private string? _engineDir;

	/// <summary>Raised after settings that affect speed changed (the toggle or the limits). May come from any thread.</summary>
	public event EventHandler? SpeedChanged;

	/// <summary>Starts tracking which folder the engine was launched with. Call once at startup.</summary>
	public void Start() {
		_engineDir = DefaultFolderResolver.ForEngine(store.Load());
		engine.StateChanged += (_, _) => {
			// The engine reads the settings itself when it starts, so a fresh start has the current folder.
			if (engine.State == EngineState.Running) {
				_engineDir = DefaultFolderResolver.ForEngine(store.Load());
			}
		};
	}

	/// <summary>The engine runs from a folder that is no longer the default folder setting; applies after a restart.</summary>
	public bool RestartRequired(AppSettings s) =>
		_engineDir is not null && !string.Equals(_engineDir, DefaultFolderResolver.ForEngine(s), StringComparison.OrdinalIgnoreCase);

	/// <summary>Sends every option to the engine if it is running. Returns false if aria2 refused or the engine is down.</summary>
	public async Task<bool> ApplyAsync(AppSettings s, CancellationToken ct = default) {
		if (engine.Rpc is not { } rpc) {
			return false;
		}
		try {
			var options = new Dictionary<string, string>(EngineOptions.ToGlobalOptions(s));
			// Force-start bookkeeping owns this option while downloads are forced; hand it the new base instead.
			options.Remove(EngineOptions.MaxConcurrent);
			await rpc.ChangeGlobalOptionAsync(options, ct);
			await operations.SetBaseConcurrentAsync(Math.Max(1, s.MaxConcurrentDownloads), ct);
			SpeedChanged?.Invoke(this, EventArgs.Empty);
			return true;
		}
		catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or TaskCanceledException) {
			logger.LogWarning(ex, "Could not apply settings to the engine");
			return false;
		}
	}

	/// <summary>Switches between the normal and the alternative speed limits and applies them right away.</summary>
	public async Task SetAltSpeedAsync(bool active, CancellationToken ct = default) {
		AppSettings s;
		lock (_lock) {
			s = store.Load();
			s.AltSpeedActive = active;
			store.Save(s);
		}
		if (engine.Rpc is { } rpc) {
			try {
				await rpc.ChangeGlobalOptionAsync(EngineOptions.SpeedOptions(s), ct);
			}
			catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or TaskCanceledException) {
				logger.LogWarning(ex, "Could not apply the speed limits");
			}
		}
		SpeedChanged?.Invoke(this, EventArgs.Empty);
	}
}
