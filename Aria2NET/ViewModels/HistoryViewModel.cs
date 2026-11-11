using System.Collections.ObjectModel;
using System.Text;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Aria2NET.ViewModels;

/// <summary>The History page: everything downloaded so far, searchable and filterable. Must be created on the UI thread.</summary>
public sealed partial class HistoryViewModel : ObservableObject {
	private static readonly string[] StatusNames = [Loc.S("History_AllStatuses", "All statuses"), Loc.S("Status_Completed", "Completed"), Loc.S("Status_Failed", "Failed"), Loc.S("Status_Removed", "Removed"), Loc.S("Status_Interrupted", "Interrupted"), Loc.S("History_InProgress", "In progress")];
	private static readonly string[] SortNames = [Loc.S("History_SortAdded", "Date added"), Loc.S("History_SortFinished", "Date finished"), Loc.S("Downloads_ColName", "Name"), Loc.S("Downloads_ColSize", "Size"), Loc.S("Downloads_ColStatus", "Status")];
	private static readonly string AllSites = Loc.S("History_AllSites", "All sites");
	private static readonly string AllTypes = Loc.S("History_AllTypes", "All types");

	private readonly HistoryService _history;
	private readonly DownloadOperations _operations;
	private readonly SettingsStore<AppSettings> _settings;
	private readonly ILogger<HistoryViewModel> _logger;
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
	private readonly DispatcherQueueTimer _debounce;
	private List<HistoryRowViewModel> _selection = [];
	private int _loadVersion;
	private bool _loadingFilters;

	public HistoryViewModel(HistoryService history, DownloadOperations operations, SettingsStore<AppSettings> settings, ILogger<HistoryViewModel> logger) {
		_history = history;
		_operations = operations;
		_settings = settings;
		_logger = logger;
		_debounce = _dispatcher.CreateTimer();
		_debounce.Interval = TimeSpan.FromMilliseconds(250);
		_debounce.IsRepeating = false;
		_debounce.Tick += async (_, _) => await ReloadAsync();
		Sites.Add(AllSites);
	}

	public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
	public ObservableCollection<string> Sites { get; } = [];
	public IReadOnlyList<string> Statuses => StatusNames;
	public IReadOnlyList<string> Types { get; } = [AllTypes, .. FileCategory.All];
	public IReadOnlyList<string> Sorts => SortNames;

	public bool IsAvailable => _history.IsAvailable;
	public bool IsUnavailable => !IsAvailable;
	public string UnavailableText => _history.Problem ?? "";

	[ObservableProperty]
	public partial string SearchText { get; set; } = "";

	[ObservableProperty]
	public partial int StatusIndex { get; set; }

	[ObservableProperty]
	public partial int TypeIndex { get; set; }

	[ObservableProperty]
	public partial int SiteIndex { get; set; }

	[ObservableProperty]
	public partial int SortIndex { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(DirectionText))]
	public partial bool Descending { get; set; } = true;

	public string DirectionText => Descending ? Loc.S("History_Descending", "Descending") : Loc.S("History_Ascending", "Ascending");

	[ObservableProperty]
	public partial DateTimeOffset? FromDate { get; set; }

	[ObservableProperty]
	public partial DateTimeOffset? ToDate { get; set; }

	[ObservableProperty]
	public partial string CountText { get; private set; } = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsFilteredEmpty), nameof(IsHistoryEmpty))]
	public partial bool HasRows { get; private set; }

	[ObservableProperty]
	public partial bool HasFilter { get; private set; }

	[ObservableProperty]
	public partial string ActionMessage { get; private set; } = "";

	[ObservableProperty]
	public partial bool HasActionMessage { get; private set; }

	[ObservableProperty]
	public partial bool CanRedownload { get; private set; }

	[ObservableProperty]
	public partial bool CanOpenFile { get; private set; }

	[ObservableProperty]
	public partial bool CanOpenFolder { get; private set; }

	[ObservableProperty]
	public partial bool CanCopyUrl { get; private set; }

	[ObservableProperty]
	public partial bool CanDelete { get; private set; }

	private bool _anyEntries;

	/// <summary>The database holds entries but none match the filters.</summary>
	public bool IsFilteredEmpty => IsAvailable && !HasRows && _anyEntries;

	/// <summary>Nothing has been recorded yet.</summary>
	public bool IsHistoryEmpty => IsAvailable && !HasRows && !_anyEntries;

	partial void OnSearchTextChanged(string value) => ScheduleReload();
	partial void OnStatusIndexChanged(int value) => ScheduleReload();
	partial void OnTypeIndexChanged(int value) => ScheduleReload();
	partial void OnSiteIndexChanged(int value) => ScheduleReload();
	partial void OnSortIndexChanged(int value) => ScheduleReload();
	partial void OnDescendingChanged(bool value) => ScheduleReload();
	partial void OnFromDateChanged(DateTimeOffset? value) => ScheduleReload();
	partial void OnToDateChanged(DateTimeOffset? value) => ScheduleReload();

	private void ScheduleReload() {
		if (_loadingFilters) {
			return;
		}
		_debounce.Stop();
		_debounce.Start();
	}

	public void ClearFilters() {
		_loadingFilters = true;
		SearchText = "";
		StatusIndex = 0;
		TypeIndex = 0;
		SiteIndex = 0;
		FromDate = null;
		ToDate = null;
		_loadingFilters = false;
		ScheduleReload();
	}

	private HistoryQuery BuildQuery(int limit = 2000) {
		var from = FromDate is { } f ? f.LocalDateTime.Date.ToUniversalTime() : (DateTime?)null;
		var to = ToDate is { } t ? t.LocalDateTime.Date.AddDays(1).ToUniversalTime() : (DateTime?)null;
		return new HistoryQuery(
			Search: SearchText,
			Statuses: StatusIndex switch {
				1 => [HistoryStatus.Complete],
				2 => [HistoryStatus.Error],
				3 => [HistoryStatus.Removed],
				4 => [HistoryStatus.Interrupted],
				5 => [HistoryStatus.Active, HistoryStatus.Waiting, HistoryStatus.Paused],
				_ => null,
			},
			FromUtc: from,
			ToUtc: to,
			Host: SiteIndex > 0 && SiteIndex < Sites.Count ? Sites[SiteIndex] : null,
			Category: TypeIndex > 0 ? Types[TypeIndex] : null,
			Sort: (HistorySort)Math.Clamp(SortIndex, 0, SortNames.Length - 1),
			Descending: Descending,
			Limit: limit);
	}

	/// <summary>Runs the query on a background thread and replaces the rows. Newer calls win over older ones.</summary>
	public async Task ReloadAsync() {
		if (_history.Store is not { } store) {
			return;
		}
		var version = ++_loadVersion;
		var query = BuildQuery();
		HasFilter = StatusIndex > 0 || TypeIndex > 0 || SiteIndex > 0 || FromDate is not null || ToDate is not null || !string.IsNullOrWhiteSpace(SearchText);
		try {
			var (entries, total, hosts) = await Task.Run(() => (store.Query(query), store.Count(), store.Hosts()));
			if (version != _loadVersion) {
				return;
			}

			var chosen = SiteIndex > 0 && SiteIndex < Sites.Count ? Sites[SiteIndex] : null;
			_loadingFilters = true;
			Sites.Clear();
			Sites.Add(AllSites);
			foreach (var host in hosts) {
				Sites.Add(host);
			}
			SiteIndex = chosen is null ? 0 : Math.Max(0, Sites.IndexOf(chosen));
			_loadingFilters = false;

			Rows.Clear();
			foreach (var entry in entries) {
				Rows.Add(new HistoryRowViewModel(entry));
			}
			_anyEntries = total > 0;
			HasRows = Rows.Count > 0;
			OnPropertyChanged(nameof(IsFilteredEmpty));
			OnPropertyChanged(nameof(IsHistoryEmpty));
			CountText = entries.Count == total ? (total == 1 ? Loc.S("History_CountOne", "1 download") : Loc.F("History_CountMany", "{0} downloads", total)) : Loc.F("History_CountOf", "{0} of {1} downloads", entries.Count, total);
			_selection = [];
			RefreshCapabilities();
			_ = MarkMissingFilesAsync(Rows.ToList(), version);
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Loading history failed");
			ShowMessage(Loc.S("History_ReadFailed", "History could not be read. Details are in the log file."));
		}
	}

	// Checking thousands of paths is slow on network drives, so the list is shown first and the "File missing" badges follow.
	private async Task MarkMissingFilesAsync(List<HistoryRowViewModel> rows, int version) {
		var missing = await Task.Run(() => rows
			.Where(r => r.Entry.Status == HistoryStatus.Complete && !string.IsNullOrEmpty(r.Entry.Path))
			.Where(r => !File.Exists(r.Entry.Path))
			.ToList());
		if (version != _loadVersion) {
			return;
		}
		foreach (var row in missing) {
			row.FileMissing = true;
		}
		RefreshCapabilities();
	}

	// ---------------------------------------------------------------- selection

	public void SetSelection(IEnumerable<HistoryRowViewModel> rows) {
		_selection = rows.ToList();
		RefreshCapabilities();
	}

	private void RefreshCapabilities() {
		var single = _selection.Count == 1 ? _selection[0] : null;
		CanRedownload = _selection.Any(r => !string.IsNullOrEmpty(r.Entry.Uri));
		CanCopyUrl = CanRedownload;
		CanOpenFile = single is { Entry: { Status: HistoryStatus.Complete, Path: { Length: > 0 } }, FileMissing: false };
		CanOpenFolder = single is { Entry.Dir.Length: > 0 };
		CanDelete = _selection.Count > 0;
	}

	public int SelectedCount => _selection.Count;

	// ---------------------------------------------------------------- actions

	/// <summary>
	/// Downloads the selected entries again as new copies, into <paramref name="folder"/> or each entry's own folder.
	/// Returns how many were started.
	/// </summary>
	public async Task<int> RedownloadAsync(string? folder) {
		ClearMessage();
		var started = 0;
		var problems = new List<string>();
		foreach (var row in _selection.Where(r => !string.IsNullOrEmpty(r.Entry.Uri)).ToList()) {
			var target = folder ?? row.Entry.Dir;
			if (string.IsNullOrEmpty(target)) {
				target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
			}
			try {
				await _operations.DownloadAgainAsync(row.Entry.Uri!, target);
				started++;
			}
			catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or InvalidOperationException) {
				_logger.LogWarning(ex, "Download again failed for {Uri}", row.Entry.Uri);
				problems.Add($"{row.Name}: {ex.Message}");
			}
		}
		if (problems.Count > 0) {
			ShowMessage(string.Join("\n", problems));
		}
		return started;
	}

	public async Task DeleteSelectedAsync() {
		if (_history.Store is not { } store || _selection.Count == 0) {
			return;
		}
		var ids = _selection.Select(r => r.Entry.Id).ToList();
		await Task.Run(() => store.Delete(ids));
		await ReloadAsync();
	}

	public async Task ClearAllAsync() {
		if (_history.Store is not { } store) {
			return;
		}
		await Task.Run(store.Clear);
		await ReloadAsync();
	}

	/// <summary>Writes everything that matches the current filters (not just the visible page) to <paramref name="path"/>.</summary>
	public async Task ExportAsync(string path, bool json) {
		if (_history.Store is not { } store) {
			return;
		}
		try {
			var entries = await Task.Run(() => store.Query(BuildQuery(limit: 100_000)));
			// Excel only recognises UTF-8 CSV with a byte-order mark.
			await File.WriteAllTextAsync(path, json ? HistoryExport.ToJson(entries) : HistoryExport.ToCsv(entries), new UTF8Encoding(encoderShouldEmitUTF8Identifier: !json));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			_logger.LogWarning(ex, "Export failed");
			ShowMessage(Loc.F("History_WriteFailed", "Could not write the file: {0}", ex.Message));
		}
	}

	public void OpenFile() {
		if (_selection is [{ Entry.Path: { Length: > 0 } path, FileMissing: false }]) {
			ReportOpen(ShellLauncher.TryOpenFile(path, out var error), path, error);
		}
	}

	public void OpenFolder() {
		if (_selection is not [{ Entry.Dir: { Length: > 0 } dir } row]) {
			return;
		}
		ReportOpen(ShellLauncher.TryOpenFolder(dir, row.Entry.Path, out var error), dir, error);
	}

	public void CopyUrl() {
		var urls = _selection.Select(r => r.Entry.Uri).Where(u => !string.IsNullOrEmpty(u)).ToList();
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

	public void ClearMessage() {
		ActionMessage = "";
		HasActionMessage = false;
	}

	public void ShowMessage(string message) {
		ActionMessage = message;
		HasActionMessage = true;
	}
}
