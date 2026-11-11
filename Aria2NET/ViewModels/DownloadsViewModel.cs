using System.Collections.ObjectModel;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using Aria2NET.Core.Torrents;
using Aria2NET.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Aria2NET.ViewModels;

/// <summary>Downloads list state and actions. Must be created on the UI thread.</summary>
public sealed partial class DownloadsViewModel : ObservableObject {
	private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

	private readonly SettingsApplier _applier;
	private readonly IAria2Engine _engine;
	private readonly DownloadOperations _ops;
	private readonly SettingsStore<AppSettings> _settings;
	private readonly HistoryService _history;
	private readonly ILogger<DownloadsViewModel> _logger;
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
	private readonly DispatcherQueueTimer _undoTimer;
	private readonly Dictionary<string, DownloadRowViewModel> _byGid = [];
	private readonly HashSet<string> _hidden = [];
	private readonly Dictionary<string, DateTimeOffset> _added = [];
	private readonly Dictionary<string, DateTimeOffset> _finished = [];
	private List<DownloadItem> _items = [];
	private List<DownloadRowViewModel> _selection = [];
	private List<DownloadItem> _pendingRemoval = [];
	private bool _baselineTaken;

	/// <summary>Rows currently shown (after filter and sort).</summary>
	public ObservableCollection<DownloadRowViewModel> Rows { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsEngineDown))]
	public partial bool IsEngineUp { get; private set; }

	/// <summary>No downloads at all (not just none matching the filter).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRows))]
	public partial bool IsEmpty { get; private set; } = true;

	[ObservableProperty]
	public partial bool IsFilteredEmpty { get; private set; }

	public bool IsEngineDown => !IsEngineUp;

	public bool HasRows => !IsEmpty;

	[ObservableProperty]
	public partial string StatsText { get; private set; } = "";

	// ---- Filter and sort ----
	[ObservableProperty]
	public partial StatusFilter Filter { get; private set; }

	[ObservableProperty]
	public partial SortColumn SortColumn { get; private set; }

	[ObservableProperty]
	public partial bool SortDescending { get; private set; }

	[ObservableProperty]
	public partial string AllLabel { get; private set; } = Loc.S("Downloads_FilterAll", "All");

	[ObservableProperty]
	public partial string DownloadingLabel { get; private set; } = Loc.S("Status_Downloading", "Downloading");

	[ObservableProperty]
	public partial string WaitingLabel { get; private set; } = Loc.S("Downloads_FilterWaiting", "Waiting");

	[ObservableProperty]
	public partial string PausedLabel { get; private set; } = Loc.S("Status_Paused", "Paused");

	[ObservableProperty]
	public partial string CompletedLabel { get; private set; } = Loc.S("Status_Completed", "Completed");

	[ObservableProperty]
	public partial string FailedLabel { get; private set; } = Loc.S("Status_Failed", "Failed");

	[ObservableProperty]
	public partial string SeedingLabel { get; private set; } = Loc.S("Status_Seeding", "Seeding");

	[ObservableProperty]
	public partial string TorrentsLabel { get; private set; } = Loc.S("Downloads_FilterTorrents", "Torrents");

	[ObservableProperty]
	public partial bool CanRecheck { get; private set; }

	[ObservableProperty]
	public partial bool CanChangeSeeding { get; private set; }

	[ObservableProperty]
	public partial bool CanShowTorrentDetails { get; private set; }

	[ObservableProperty]
	public partial string NameHeader { get; private set; } = Loc.S("Downloads_ColName", "Name");

	[ObservableProperty]
	public partial string SizeHeader { get; private set; } = Loc.S("Downloads_ColSize", "Size");

	[ObservableProperty]
	public partial string ProgressHeader { get; private set; } = Loc.S("Downloads_ColProgress", "Progress");

	[ObservableProperty]
	public partial string SpeedHeader { get; private set; } = Loc.S("Downloads_ColSpeed", "Speed");

	[ObservableProperty]
	public partial string EtaHeader { get; private set; } = Loc.S("Downloads_ColEta", "ETA");

	[ObservableProperty]
	public partial string StatusHeader { get; private set; } = Loc.S("Downloads_ColStatus", "Status");

	// ---- What the current selection allows ----
	[ObservableProperty]
	public partial bool CanPause { get; private set; }

	[ObservableProperty]
	public partial bool CanResume { get; private set; }

	[ObservableProperty]
	public partial bool CanRetry { get; private set; }

	[ObservableProperty]
	public partial bool CanRemove { get; private set; }

	[ObservableProperty]
	public partial bool CanForceStart { get; private set; }

	[ObservableProperty]
	public partial bool CanMove { get; private set; }

	[ObservableProperty]
	public partial bool CanRename { get; private set; }

	[ObservableProperty]
	public partial bool CanChangeFolder { get; private set; }

	[ObservableProperty]
	public partial bool CanSetSpeedLimit { get; private set; }

	[ObservableProperty]
	public partial bool CanOpenFile { get; private set; }

	[ObservableProperty]
	public partial bool CanOpenFolder { get; private set; }

	[ObservableProperty]
	public partial bool CanCopyUrl { get; private set; }

	[ObservableProperty]
	public partial bool CanShowProperties { get; private set; }

	[ObservableProperty]
	public partial bool CanPauseAll { get; private set; }

	[ObservableProperty]
	public partial bool CanResumeAll { get; private set; }

	[ObservableProperty]
	public partial bool CanClearCompleted { get; private set; }

	[ObservableProperty]
	public partial bool CanClearFailed { get; private set; }

	// ---- Messages ----
	[ObservableProperty]
	public partial string ActionMessage { get; private set; } = "";

	[ObservableProperty]
	public partial bool HasActionMessage { get; private set; }

	[ObservableProperty]
	public partial string UndoText { get; private set; } = "";

	[ObservableProperty]
	public partial bool IsUndoVisible { get; private set; }

	public bool ConfirmDeleteFiles => _settings.Load().ConfirmDeleteFiles;

	public DownloadsViewModel(IAria2Engine engine, DownloadMonitor monitor, DownloadOperations ops, TorrentOperations torrents, NoticeCenter notices, SettingsStore<AppSettings> settings, HistoryService history, SettingsApplier applier, ILogger<DownloadsViewModel> logger) {
		_applier = applier;
		_torrents = torrents;
		_notices = notices;
		_engine = engine;
		_ops = ops;
		_settings = settings;
		_history = history;
		_logger = logger;

		var saved = settings.Load();
		Filter = Enum.TryParse<StatusFilter>(saved.StatusFilter, out var f) ? f : StatusFilter.All;
		SortColumn = Enum.TryParse<SortColumn>(saved.SortColumn, out var c) ? c : SortColumn.None;
		SortDescending = saved.SortDescending;
		RefreshHeaders();

		_undoTimer = _dispatcher.CreateTimer();
		_undoTimer.Interval = UndoWindow;
		_undoTimer.IsRepeating = false;
		_undoTimer.Tick += async (_, _) => await FinalizePendingAsync();

		monitor.SnapshotReady += (_, snapshot) => _dispatcher.TryEnqueue(() => Apply(snapshot));
		_engine.StateChanged += (_, _) => _dispatcher.TryEnqueue(RefreshEngine);
		RefreshEngine();
		applier.SpeedChanged += (_, _) => _dispatcher.TryEnqueue(RefreshAltSpeed);
		RefreshAltSpeed();
	}

	// ---------------------------------------------------------------- adding

	/// <summary>Folder the Add dialog starts with: last used, else the configured default, else Downloads.</summary>
	public string InitialFolder() => DefaultFolderResolver.ForAddDialog(_settings.Load());

	// ---------------------------------------------------------------- alternative speeds

	/// <summary>The alternative speed limits are in force (the toolbar toggle).</summary>
	[ObservableProperty]
	public partial bool AltSpeedActive { get; private set; }

	[ObservableProperty]
	public partial string AltSpeedTip { get; private set; } = "";

	private void RefreshAltSpeed() {
		var s = _settings.Load();
		AltSpeedActive = s.AltSpeedActive;
		var (down, up) = (s.AltDownloadLimitKiBps, s.AltUploadLimitKiBps);
		static string Limit(int kib) => kib <= 0 ? Loc.S("Downloads_Unlimited", "unlimited") : $"{kib} KiB/s";
		AltSpeedTip = AltSpeedActive
			? Loc.S("Downloads_AltSpeedOn", "Alternative speeds are on. Click to go back to the normal limits.")
			: Loc.F("Downloads_AltSpeedTip", "Switch to alternative speeds (download {0}, upload {1}). Set them in Settings.", Limit(down), Limit(up));
	}

	public Task SetAltSpeedAsync(bool active) => _applier.SetAltSpeedAsync(active);

	/// <summary>Set by the page: asks what to do about a file that already exists (the "Ask" name-clash setting). Without it, both files are kept.</summary>
	public Func<string, Task<ConflictChoice>>? PromptConflict { get; set; }

	/// <summary>Adds each link or file as its own download. Returns an error message, or null when everything was accepted.</summary>
	public async Task<string?> AddAsync(IReadOnlyList<string> urls, string folder, string? fileName, AddOptions? extra = null) {
		var rpc = _engine.Rpc;
		if (rpc is null) {
			return Loc.S("Downloads_EngineNotRunning", "The download engine is not running.");
		}
		if (extra?.Validate() is { } invalid) {
			return invalid;
		}

		var failures = new List<string>();
		var accepted = 0;
		foreach (var url in urls) {
			try {
				switch (UrlParser.Classify(url)) {
					case LinkKind.TorrentFile:
						if (await AddTorrentFileAsync(url, folder, extra)) {
							accepted++;
						}
						continue;
					case LinkKind.MetalinkFile:
						await AddMetalinkFileAsync(url, folder, extra);
						accepted++;
						continue;
					case LinkKind.Magnet:
						if (await AddMagnetAsync(url, folder, extra)) {
							accepted++;
						}
						continue;
				}

				var options = (extra ?? AddOptions.None).ToOptions().ToDictionary(p => p.Key, p => p.Value);
				options["dir"] = folder;
				if (urls.Count == 1 && !string.IsNullOrWhiteSpace(fileName)) {
					options["out"] = fileName.Trim();
				}
				if (FileConflicts.FindExisting(folder, url, urls.Count == 1 ? fileName : null) is { } existing) {
					var policy = _settings.Load().Conflict;
					var choice = FileConflicts.FromPolicy(policy)
						?? (PromptConflict is { } ask ? await ask(existing) : ConflictChoice.Rename);
					if (choice == ConflictChoice.Skip) {
						continue;
					}
					foreach (var (k, v) in FileConflicts.OptionsFor(choice)) {
						options[k] = v;
					}
				}
				var gid = await rpc.AddUriAsync([url], options);
				_added[gid] = DateTimeOffset.Now;
				accepted++;
			}
			catch (Exception ex) {
				_logger.LogWarning(ex, "Adding {Url} failed", url);
				failures.Add($"{url}: {ex.Message}");
			}
		}

		if (accepted > 0) {
			SaveSettings(s => s.LastUsedFolder = folder);
		}
		return failures.Count == 0 ? null : string.Join("\n", failures);
	}

	// ---------------------------------------------------------------- selection

	/// <summary>Called by the page whenever the ListView selection changes.</summary>
	/// <summary>Raised when re-sorting rows may have dropped the ListView selection; the page re-selects, then calls <see cref="FinishRestore"/>.</summary>
	public event EventHandler<IReadOnlyList<DownloadRowViewModel>>? SelectionRestoreNeeded;

	private List<DownloadRowViewModel>? _restoring;
	private bool _syncing;

	public void SetSelection(IEnumerable<DownloadRowViewModel> rows) {
		// While rows are being moved the ListView reports a transient empty selection; keep the real one until restored.
		if (_restoring is not null || _syncing) {
			return;
		}
		_selection = rows.ToList();
		RefreshCapabilities();
	}

	public void FinishRestore(IEnumerable<DownloadRowViewModel> actual) {
		_restoring = null;
		SetSelection(actual);
	}

	private List<DownloadItem> SelectedItems() => _selection.Select(r => r.Item).ToList();

	private void RefreshCapabilities() {
		var items = SelectedItems();
		var single = items.Count == 1 ? items[0] : null;
		CanPause = items.Any(DownloadCapabilities.CanPause);
		CanResume = items.Any(DownloadCapabilities.CanResume);
		CanRetry = items.Any(DownloadCapabilities.CanRetry);
		CanRemove = items.Count > 0;
		CanForceStart = items.Any(DownloadCapabilities.CanForceStart);
		// Queue order only makes sense while the list is shown in aria2's order.
		CanMove = single is not null && DownloadCapabilities.CanMove(single) && SortColumn == SortColumn.None;
		CanRename = single is not null && DownloadCapabilities.CanRename(single);
		CanChangeFolder = items.Count > 0 && items.All(DownloadCapabilities.CanChangeFolder);
		CanSetSpeedLimit = items.Any(DownloadCapabilities.CanSetSpeedLimit);
		CanOpenFile = single is not null && DownloadCapabilities.CanOpenFile(single);
		CanOpenFolder = single is not null && DownloadCapabilities.CanOpenFolder(single);
		CanCopyUrl = items.Any(i => !string.IsNullOrEmpty(i.FirstUri));
		CanShowProperties = single is not null;
		CanRecheck = items.Count > 0 && items.All(DownloadCapabilities.CanRecheck);
		CanChangeSeeding = items.Count > 0 && items.All(DownloadCapabilities.CanChangeSeeding);
		CanShowTorrentDetails = single is { IsTorrent: true, IsMetadata: false };

		var visible = _items.Where(i => !_hidden.Contains(i.Gid)).ToList();
		CanPauseAll = IsEngineUp && visible.Any(i => i.Status is DownloadStatus.Active or DownloadStatus.Seeding or DownloadStatus.Waiting);
		CanResumeAll = IsEngineUp && visible.Any(i => i.Status == DownloadStatus.Paused);
		CanClearCompleted = visible.Any(i => i.Status == DownloadStatus.Complete);
		CanClearFailed = visible.Any(i => i.Status is DownloadStatus.Error or DownloadStatus.Removed);
	}

	// ---------------------------------------------------------------- actions

	public Task PauseSelectedAsync() => Run(() => _ops.PauseAsync(SelectedItems()));

	public Task ResumeSelectedAsync() => Run(() => _ops.ResumeAsync(SelectedItems()));

	/// <summary>Space: pause what is running, resume what is paused.</summary>
	public Task TogglePauseSelectedAsync() => CanPause ? PauseSelectedAsync() : CanResume ? ResumeSelectedAsync() : Task.CompletedTask;

	public Task PauseAllAsync() => Run(() => _ops.PauseAllAsync());

	public Task ResumeAllAsync() => Run(() => _ops.ResumeAllAsync());

	public Task RetrySelectedAsync() => Run(async () => {
		foreach (var item in SelectedItems().Where(DownloadCapabilities.CanRetry)) {
			_added[await _ops.RetryAsync(item)] = DateTimeOffset.Now;
		}
	});

	public Task ForceStartSelectedAsync() => Run(async () => {
		foreach (var item in SelectedItems().Where(DownloadCapabilities.CanForceStart)) {
			await _ops.ForceStartAsync(item);
		}
	});

	public Task MoveSelectedAsync(QueueMove move) => Run(async () => {
		if (SelectedItems() is [{ } item] && DownloadCapabilities.CanMove(item)) {
			await _ops.MoveAsync(item, move);
		}
	});

	public Task ClearAsync(bool failed) => Run(() => _ops.ClearAsync(
		_items.Where(i => failed ? i.Status is DownloadStatus.Error or DownloadStatus.Removed : i.Status == DownloadStatus.Complete)));

	public Task RenameSelectedAsync(string newName) => Run(async () => {
		if (SelectedItems() is [{ } item]) {
			await _ops.RenameAsync(item, newName);
		}
	});

	public Task SetFolderSelectedAsync(string folder) => Run(async () => {
		foreach (var item in SelectedItems().Where(DownloadCapabilities.CanChangeFolder)) {
			await _ops.SetFolderAsync(item, folder);
		}
	});

	public Task SetSpeedLimitSelectedAsync(long bytesPerSecond) => Run(async () => {
		foreach (var item in SelectedItems().Where(DownloadCapabilities.CanSetSpeedLimit)) {
			await _ops.SetSpeedLimitAsync(item, bytesPerSecond);
		}
	});

	public int SelectedCount => _selection.Count;

	public DownloadItem? SingleSelected() => SelectedItems() is [{ } item] ? item : null;

	public async Task<DownloadDetails?> GetDetailsAsync() {
		if (SingleSelected() is not { } item) {
			return null;
		}
		try {
			return await _ops.GetDetailsAsync(item.Gid);
		}
		catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or InvalidOperationException) {
			ShowMessage(ex.Message);
			return null;
		}
	}

	/// <summary>When the download was added and when it finished: from history, or from what this run saw if history is off.</summary>
	public (DateTimeOffset? Added, DateTimeOffset? Finished) Times(string gid) {
		try {
			if (_history.Store?.Get(gid) is { } entry) {
				return (new DateTimeOffset(entry.AddedUtc), entry.FinishedUtc is { } f ? new DateTimeOffset(f) : null);
			}
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Could not read history for {Gid}", gid);
		}
		return (_added.TryGetValue(gid, out var a) ? a : null, _finished.TryGetValue(gid, out var fin) ? fin : null);
	}

	public void OpenFile() {
		if (SingleSelected() is { Path: { } path } item && DownloadCapabilities.CanOpenFile(item)) {
			ReportOpen(ShellLauncher.TryOpenFile(path, out var error), path, error);
		}
	}

	public void OpenFolder() {
		if (SingleSelected() is not { Dir: { Length: > 0 } dir } item) {
			return;
		}
		ReportOpen(ShellLauncher.TryOpenFolder(dir, item.Path, out var error), dir, error);
	}

	public void CopyUrl() {
		var urls = SelectedItems().Select(i => i.FirstUri).Where(u => !string.IsNullOrEmpty(u)).ToList();
		if (urls.Count == 0) {
			return;
		}
		var package = new DataPackage();
		package.SetText(string.Join(Environment.NewLine, urls));
		Clipboard.SetContent(package);
	}

	private void ReportOpen(bool opened, string target, string error) {
		if (!opened) {
			_logger.LogWarning("Could not open {Target}: {Error}", target, error);
			ShowMessage(Loc.F("Downloads_OpenFailed", "Could not open it: {0}", error));
		}
	}

	// ---------------------------------------------------------------- remove (with undo)

	/// <summary>Removes the selection. Without file deletion the removal is deferred for a few seconds so it can be undone.</summary>
	public async Task RemoveSelectedAsync(bool deleteFiles) {
		var items = SelectedItems();
		if (items.Count == 0) {
			return;
		}
		if (deleteFiles) {
			await Run(async () => {
				var problems = await _ops.RemoveAsync(items, deleteFiles: true);
				if (problems.Count > 0) {
					ShowMessage("Some files could not be deleted:\n" + string.Join("\n", problems));
				}
			});
			return;
		}

		await FinalizePendingAsync();
		_pendingRemoval = items;
		foreach (var item in items) {
			_hidden.Add(item.Gid);
		}
		UndoText = items.Count == 1 ? Loc.F("Downloads_RemovedOne", "Removed “{0}”", items[0].Name) : Loc.F("Downloads_RemovedMany", "Removed {0} downloads", items.Count);
		IsUndoVisible = true;
		Rebuild();
		_undoTimer.Stop();
		_undoTimer.Start();
	}

	public void UndoRemove() {
		_undoTimer.Stop();
		foreach (var item in _pendingRemoval) {
			_hidden.Remove(item.Gid);
		}
		_pendingRemoval = [];
		IsUndoVisible = false;
		Rebuild();
	}

	/// <summary>Makes a pending removal final. Also called when the window closes.</summary>
	public async Task FinalizePendingAsync() {
		_undoTimer.Stop();
		var pending = _pendingRemoval;
		_pendingRemoval = [];
		IsUndoVisible = false;
		if (pending.Count == 0) {
			return;
		}
		try {
			await _ops.RemoveAsync(pending, deleteFiles: false);
		}
		catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or InvalidOperationException or TaskCanceledException) {
			_logger.LogWarning(ex, "Removal failed");
			foreach (var item in pending) {
				_hidden.Remove(item.Gid);
			}
			ShowMessage(Loc.F("Downloads_RemoveFailed", "Could not remove: {0}", ex.Message));
			Rebuild();
		}
	}

	public void DisableDeleteConfirmation() => SaveSettings(s => s.ConfirmDeleteFiles = false);

	// ---------------------------------------------------------------- filter and sort

	public void SetFilter(StatusFilter filter) {
		if (Filter == filter) {
			return;
		}
		Filter = filter;
		SaveSettings(s => s.StatusFilter = filter.ToString());
		Rebuild();
	}

	/// <summary>Clicking a header: ascending, then descending, then back to aria2's order.</summary>
	public void ToggleSort(SortColumn column) {
		if (SortColumn != column) {
			SortColumn = column;
			SortDescending = false;
		}
		else if (!SortDescending) {
			SortDescending = true;
		}
		else {
			SortColumn = SortColumn.None;
			SortDescending = false;
		}
		SaveSettings(s => {
			s.SortColumn = SortColumn.ToString();
			s.SortDescending = SortDescending;
		});
		RefreshHeaders();
		Rebuild();
	}

	private void RefreshHeaders() {
		string H(string text, SortColumn column) => SortColumn == column ? $"{text} {(SortDescending ? "▼" : "▲")}" : text;
		NameHeader = H(Loc.S("Downloads_ColName", "Name"), SortColumn.Name);
		SizeHeader = H(Loc.S("Downloads_ColSize", "Size"), SortColumn.Size);
		ProgressHeader = H(Loc.S("Downloads_ColProgress", "Progress"), SortColumn.Progress);
		SpeedHeader = H(Loc.S("Downloads_ColSpeed", "Speed"), SortColumn.Speed);
		EtaHeader = H(Loc.S("Downloads_ColEta", "ETA"), SortColumn.Eta);
		StatusHeader = H(Loc.S("Downloads_ColStatus", "Status"), SortColumn.Status);
	}

	// ---------------------------------------------------------------- snapshots

	private void RefreshEngine() {
		IsEngineUp = _engine.State == EngineState.Running;
		if (!IsEngineUp) {
			// Stale speeds and "Downloading" rows would mislead; the session restores them after a restart.
			_items = [];
			_byGid.Clear();
			_hidden.Clear();
			_baselineTaken = false;
			Rows.Clear();
			StatsText = "";
			IsEmpty = true;
			IsFilteredEmpty = false;
			RefreshCapabilities();
		}
	}

	private void Apply(PollSnapshot snapshot) {
		if (!IsEngineUp) {
			return;
		}

		TrackTimes(snapshot.Items);
		_items = snapshot.Items.Where(i => !DownloadView.IsSuperseded(i)).ToList();
		_hidden.RemoveWhere(gid => !snapshot.Items.Any(i => i.Gid == gid));
		CheckDiskSpace(_items);

		var stats = snapshot.Stats;
		StatsText = Loc.F("Downloads_Stats", "↓ {0}  ·  {1} active  ·  {2} waiting", ByteFormat.Speed(stats.DownloadSpeed), stats.NumActive, stats.NumWaiting);
		Rebuild();
		_ = RestoreForcedLimitAsync(snapshot);
	}

	private async Task RestoreForcedLimitAsync(PollSnapshot snapshot) {
		try {
			await _ops.OnSnapshot(snapshot);
		}
		catch (Exception ex) {
			_logger.LogWarning(ex, "Could not update the force-start limit");
		}
	}

	private void TrackTimes(IReadOnlyList<DownloadItem> items) {
		var now = DateTimeOffset.Now;
		foreach (var item in items) {
			var known = _byGid.TryGetValue(item.Gid, out var row);
			// Downloads already present at launch have no known add time (that needs history, 0.4).
			if (_baselineTaken && !known) {
				_added.TryAdd(item.Gid, now);
			}
			if (known && DownloadCapabilities.IsLive(row!.Item) && item.Status is DownloadStatus.Complete or DownloadStatus.Error) {
				_finished[item.Gid] = now;
			}
		}
		_baselineTaken = true;
	}

	/// <summary>Updates the rows from the latest snapshot, filter and sort. Rows are changed in place, never rebuilt.</summary>
	private void Rebuild() {
		var visible = _items.Where(i => !_hidden.Contains(i.Gid)).ToList();

		// Keep a row object per GID that still exists, so selection and scroll survive refreshes.
		var present = new HashSet<string>(_items.Select(i => i.Gid));
		foreach (var gid in _byGid.Keys.Where(g => !present.Contains(g)).ToList()) {
			_byGid.Remove(gid);
		}
		foreach (var item in _items) {
			if (_byGid.TryGetValue(item.Gid, out var row)) {
				row.Update(item);
			}
			else {
				_byGid[item.Gid] = new DownloadRowViewModel(item);
			}
		}

		var desired = DownloadView.Apply(visible, Filter, SortColumn, SortDescending).Select(i => _byGid[i.Gid]).ToList();
		// ListView reports a lost selection while rows move, so remember it first and ignore those events.
		var before = _selection.ToList();
		bool changed;
		_syncing = true;
		try {
			changed = SyncRows(desired);
		}
		finally {
			_syncing = false;
		}
		if (changed && before.Count > 0 && _restoring is null && SelectionRestoreNeeded is { } handler) {
			_restoring = before;
			handler(this, _restoring);
		}

		IsEmpty = visible.Count == 0;
		IsFilteredEmpty = visible.Count > 0 && desired.Count == 0;
		AllLabel = Label(Loc.S("Downloads_FilterAll", "All"), StatusFilter.All, visible);
		DownloadingLabel = Label(Loc.S("Status_Downloading", "Downloading"), StatusFilter.Downloading, visible);
		WaitingLabel = Label(Loc.S("Downloads_FilterWaiting", "Waiting"), StatusFilter.Waiting, visible);
		PausedLabel = Label(Loc.S("Status_Paused", "Paused"), StatusFilter.Paused, visible);
		CompletedLabel = Label(Loc.S("Status_Completed", "Completed"), StatusFilter.Completed, visible);
		FailedLabel = Label(Loc.S("Status_Failed", "Failed"), StatusFilter.Failed, visible);
		SeedingLabel = Label(Loc.S("Status_Seeding", "Seeding"), StatusFilter.Seeding, visible);
		TorrentsLabel = Label(Loc.S("Downloads_FilterTorrents", "Torrents"), StatusFilter.Torrents, visible);
		RefreshCapabilities();
	}

	private static string Label(string name, StatusFilter filter, List<DownloadItem> items) {
		var count = DownloadView.Count(filter, items);
		return count == 0 ? name : $"{name} ({count})";
	}

	private bool SyncRows(List<DownloadRowViewModel> desired) {
		var changed = false;
		var keep = new HashSet<DownloadRowViewModel>(desired);
		for (var i = Rows.Count - 1; i >= 0; i--) {
			if (!keep.Contains(Rows[i])) {
				Rows.RemoveAt(i);
				changed = true;
			}
		}
		for (var i = 0; i < desired.Count; i++) {
			if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i])) {
				continue;
			}
			changed = true;
			var current = Rows.IndexOf(desired[i]);
			if (current >= 0) {
				Rows.Move(current, i);
			}
			else {
				Rows.Insert(i, desired[i]);
			}
		}
		return changed;
	}

	// ---------------------------------------------------------------- helpers

	private async Task Run(Func<Task> action) {
		ClearMessage();
		try {
			await action();
		}
		// TaskCanceledException is the 5 s RPC timeout: aria2 does not answer while it zero-fills a big file.
		catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or InvalidOperationException or ArgumentException or TaskCanceledException) {
			_logger.LogWarning(ex, "Download action failed");
			ShowMessage(ex is TaskCanceledException ? Loc.S("Downloads_Busy", "aria2 is busy preparing files and did not answer in time. Try again in a moment.") : ex.Message);
		}
	}

	public void ClearMessage() {
		ActionMessage = "";
		HasActionMessage = false;
	}

	private void ShowMessage(string message) {
		ActionMessage = message;
		HasActionMessage = true;
	}

	private void SaveSettings(Action<AppSettings> change) {
		try {
			var s = _settings.Load();
			change(s);
			_settings.Save(s);
		}
		catch (IOException ex) {
			_logger.LogWarning(ex, "Could not save settings");
		}
	}
}
