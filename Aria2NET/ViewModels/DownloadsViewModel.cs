using System.Collections.ObjectModel;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Aria2NET.ViewModels;

/// <summary>Downloads list state. Must be created on the UI thread.</summary>
public sealed partial class DownloadsViewModel : ObservableObject {
	private readonly IAria2Engine _engine;
	private readonly SettingsStore<AppSettings> _settings;
	private readonly ILogger<DownloadsViewModel> _logger;
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
	private readonly Dictionary<string, DownloadRowViewModel> _byGid = [];

	public ObservableCollection<DownloadRowViewModel> Rows { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsEngineDown))]
	public partial bool IsEngineUp { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRows))]
	public partial bool IsEmpty { get; private set; } = true;

	public bool IsEngineDown => !IsEngineUp;

	public bool HasRows => !IsEmpty;

	[ObservableProperty]
	public partial string StatsText { get; private set; } = "";

	public DownloadsViewModel(IAria2Engine engine, DownloadMonitor monitor, SettingsStore<AppSettings> settings, ILogger<DownloadsViewModel> logger) {
		_engine = engine;
		_settings = settings;
		_logger = logger;
		monitor.SnapshotReady += (_, snapshot) => _dispatcher.TryEnqueue(() => Apply(snapshot));
		_engine.StateChanged += (_, _) => _dispatcher.TryEnqueue(RefreshEngine);
		RefreshEngine();
	}

	/// <summary>Folder the Add dialog starts with: last used, else the configured default, else Downloads.</summary>
	public string InitialFolder() {
		var s = _settings.Load();
		foreach (var candidate in new[] { s.LastUsedFolder, s.DefaultDownloadFolder }) {
			if (!string.IsNullOrWhiteSpace(candidate)) {
				return candidate;
			}
		}
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
	}

	/// <summary>Adds each URL as its own download. Returns an error message, or null when everything was accepted.</summary>
	public async Task<string?> AddAsync(IReadOnlyList<string> urls, string folder, string? fileName) {
		var rpc = _engine.Rpc;
		if (rpc is null) {
			return "The download engine is not running.";
		}

		var failures = new List<string>();
		foreach (var url in urls) {
			var options = new Dictionary<string, string> { ["dir"] = folder };
			if (urls.Count == 1 && !string.IsNullOrWhiteSpace(fileName)) {
				options["out"] = fileName.Trim();
			}
			try {
				await rpc.AddUriAsync([url], options);
			}
			catch (Exception ex) {
				_logger.LogWarning(ex, "addUri failed for {Url}", url);
				failures.Add($"{url}: {ex.Message}");
			}
		}

		if (failures.Count < urls.Count) {
			var s = _settings.Load();
			s.LastUsedFolder = folder;
			try {
				_settings.Save(s);
			}
			catch (IOException ex) {
				_logger.LogWarning(ex, "Could not remember the last used folder");
			}
		}
		return failures.Count == 0 ? null : string.Join("\n", failures);
	}

	private void RefreshEngine() {
		IsEngineUp = _engine.State == EngineState.Running;
		if (!IsEngineUp) {
			// Stale speeds and "Downloading" rows would mislead; the session restores them after a restart.
			Rows.Clear();
			_byGid.Clear();
			StatsText = "";
			IsEmpty = true;
		}
	}

	private void Apply(PollSnapshot snapshot) {
		if (!IsEngineUp) {
			return;
		}

		var seen = new HashSet<string>();
		for (var i = 0; i < snapshot.Items.Count; i++) {
			var item = snapshot.Items[i];
			seen.Add(item.Gid);
			if (_byGid.TryGetValue(item.Gid, out var row)) {
				row.Update(item);
				var current = Rows.IndexOf(row);
				if (current != i) {
					Rows.Move(current, i);
				}
			}
			else {
				row = new DownloadRowViewModel(item);
				_byGid[item.Gid] = row;
				Rows.Insert(i, row);
			}
		}

		for (var i = Rows.Count - 1; i >= 0; i--) {
			if (!seen.Contains(Rows[i].Gid)) {
				_byGid.Remove(Rows[i].Gid);
				Rows.RemoveAt(i);
			}
		}

		IsEmpty = Rows.Count == 0;
		var stats = snapshot.Stats;
		StatsText = $"↓ {ByteFormat.Speed(stats.DownloadSpeed)}  ·  {stats.NumActive} active  ·  {stats.NumWaiting} waiting";
	}
}
