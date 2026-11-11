using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Settings;

/// <summary>Pushes settings to the running engine with <c>changeGlobalOption</c>, and owns the alternative-speed toggle.</summary>
public sealed class SettingsApplier(IAria2Engine engine, SettingsStore<AppSettings> store, DownloadOperations operations, ILogger<SettingsApplier> logger, AppPaths? paths = null) {
	private readonly Lock _lock = new();
	private RestartOnly? _started;

	/// <summary>The settings aria2 reads only at launch (it ignores them in <c>changeGlobalOption</c>).</summary>
	private sealed record RestartOnly(string Folder, IReadOnlyDictionary<string, string> Launch, IReadOnlyDictionary<string, string> Raw) {
		public static RestartOnly Of(AppSettings s) => new(
			DefaultFolderResolver.ForEngine(s),
			EngineOptions.ToGlobalOptions(s).Where(o => EngineOptions.LaunchOnlyKeys.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value),
			// Hand-typed options may be launch-only too and there is no way to tell, so any change suggests a restart.
			new Dictionary<string, string>(s.RawOptions ?? []));

		private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
			a.Count == b.Count && a.All(o => b.TryGetValue(o.Key, out var v) && v == o.Value);

		public bool Matches(RestartOnly other) =>
			string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase) && Same(Launch, other.Launch) && Same(Raw, other.Raw);
	}

	/// <summary>Raised after settings that affect speed changed (the toggle or the limits). May come from any thread.</summary>
	public event EventHandler? SpeedChanged;

	/// <summary>Starts tracking which launch-only settings the engine was started with. Call once at startup, before the engine starts.</summary>
	public void Start() {
		_started = RestartOnly.Of(store.Load());
		engine.StateChanged += (_, _) => {
			// The engine reads the settings itself when it starts, so a fresh start has the current values.
			if (engine.State == EngineState.Running) {
				_started = RestartOnly.Of(store.Load());
			}
		};
	}

	/// <summary>A setting that aria2 only reads at launch (default folder, certificate check, IPv6) differs from what the engine runs with.</summary>
	public bool RestartRequired(AppSettings s) => _started is { } started && !started.Matches(RestartOnly.Of(s));

	/// <summary>Sends every option to the engine if it is running. Returns false if aria2 refused or the engine is down.</summary>
	public async Task<bool> ApplyAsync(AppSettings s, CancellationToken ct = default) {
		if (engine.Rpc is not { } rpc) {
			return false;
		}
		try {
			var options = new Dictionary<string, string>(EngineOptions.LiveOptions(s));
			// Hand-typed options go one by one afterwards: aria2 may refuse one, and that must not stop the rest from being applied.
			var raw = (s.RawOptions ?? []).Where(o => RawOptionRules.Validate(o.Key, o.Value) is null).ToList();
			foreach (var (key, _) in raw) {
				options.Remove(key);
			}
			// Force-start bookkeeping owns this option while downloads are forced; hand it the new base instead.
			options.Remove(EngineOptions.MaxConcurrent);
			// The extra trackers come from the cached list (or none when switched off); the host refreshes the cache itself.
			if (s.Trackers == TrackerSource.Off) {
				options["bt-tracker"] = "";
			}
			else if (paths is not null) {
				options["bt-tracker"] = Aria2Config.TrackerOptionValue(Torrents.TrackerListService.ReadCache(paths));
			}
			await rpc.ChangeGlobalOptionAsync(options, ct);
			await operations.SetBaseConcurrentAsync(Math.Max(1, s.MaxConcurrentDownloads), ct);
			var allApplied = true;
			foreach (var (key, value) in raw) {
				try {
					await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { [key] = value }, ct);
				}
				catch (Aria2RpcException ex) {
					allApplied = false;
					logger.LogWarning("aria2 refused the option {Key}: {Message}", key, ex.Message);
				}
			}
			SpeedChanged?.Invoke(this, EventArgs.Empty);
			return allApplied;
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
