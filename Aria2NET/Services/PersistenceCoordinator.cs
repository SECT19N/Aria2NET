using Aria2NET.Core.Aria2;
using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Services;

/// <summary>Connects the history recorder, engine restarts and startup checks to the rest of the app.</summary>
public sealed class PersistenceCoordinator(
	IAria2Engine engine,
	DownloadMonitor monitor,
	HistoryService history,
	EngineSupervisor supervisor,
	DownloadOperations operations,
	SettingsStore<AppSettings> settings,
	NoticeCenter notices,
	ILogger<PersistenceCoordinator> logger) {

	private int _scanned;

	/// <summary>Call once, on the UI thread, after the window exists.</summary>
	public void Start() {
		if (history.Recorder is { } recorder) {
			monitor.SnapshotReady += (_, snapshot) => recorder.OnSnapshot(snapshot);
			engine.StateChanged += (_, _) => {
				if (engine.State == EngineState.Running) {
					recorder.EngineStarted();
				}
			};
			if (engine.State == EngineState.Running) {
				recorder.EngineStarted();
			}
		}

		engine.NoticeRaised += (_, notice) =>
			notices.Post(new Notice("Unfinished downloads list repaired", notice.Message, InfoBarSeverity.Warning));
		supervisor.Restarted += (_, _) =>
			notices.Post(new Notice("The download engine restarted", "aria2 stopped unexpectedly and was started again. Your unfinished downloads were picked up where the last save left them.", InfoBarSeverity.Informational));

		ReportHistoryProblems();
		monitor.SnapshotReady += OnFirstSnapshot;
		_ = Task.Run(ApplyRetention);
	}

	private void ReportHistoryProblems() {
		if (history.Problem is { } problem) {
			notices.Post(new Notice("History is off", problem, InfoBarSeverity.Warning));
		}
		else if (history.OpenResult is { Status: DatabaseStatus.Recovered, MovedTo: { } moved }) {
			notices.Post(new Notice("History was damaged", $"The history file could not be read, so a new one was started. The old file was kept as {moved}.", InfoBarSeverity.Warning));
		}
	}

	private void ApplyRetention() {
		try {
			var removed = history.ApplyRetention(settings.Load().HistoryRetentionDays);
			if (removed > 0) {
				logger.LogInformation("History retention removed {Count} old entries", removed);
			}
		}
		catch (Exception ex) {
			logger.LogWarning(ex, "Applying the history retention setting failed");
		}
	}

	// Runs once per launch, on the first snapshot after aria2 is up (so history is reconciled and aria2's own list is known).
	private void OnFirstSnapshot(object? sender, PollSnapshot snapshot) {
		if (Interlocked.Exchange(ref _scanned, 1) != 0) {
			return;
		}
		monitor.SnapshotReady -= OnFirstSnapshot;
		_ = Task.Run(() => ScanLeftovers(snapshot));
	}

	private void ScanLeftovers(PollSnapshot snapshot) {
		try {
			var saved = settings.Load();
			var folders = new List<string>();
			if (history.Store is { } store) {
				folders.AddRange(store.Folders());
			}
			folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
			folders.AddRange(new[] { saved.DefaultDownloadFolder, saved.LastUsedFolder }.Where(f => !string.IsNullOrWhiteSpace(f))!);

			var candidates = OrphanFiles.Find(folders, snapshot.Items)
				.Select(o => (Orphan: o, Entry: history.Store?.FindByPath(o.DataFile)))
				// A download the user removed or that failed is not "lost": don't nag about it.
				.Where(x => x.Entry is null || x.Entry.Status is HistoryStatus.Interrupted or HistoryStatus.Active or HistoryStatus.Waiting or HistoryStatus.Paused)
				.ToList();
			if (candidates.Count == 0) {
				return;
			}

			var resumable = candidates.Where(x => !string.IsNullOrEmpty(x.Entry?.Uri)).ToList();
			var message = $"{candidates.Count} partly downloaded {(candidates.Count == 1 ? "file was" : "files were")} left behind by an earlier session."
				+ (resumable.Count > 0 ? $" {resumable.Count} can be resumed." : " They cannot be resumed because their links are not known.");

			notices.Post(new Notice("Unfinished downloads found", message, InfoBarSeverity.Informational,
				resumable.Count == 0 ? null : new NoticeAction($"Resume {resumable.Count}", () => ResumeAsync(resumable)),
				new NoticeAction("Delete leftovers", () => DeleteAsync(candidates.Select(c => c.Orphan).ToList()))));
		}
		catch (Exception ex) {
			logger.LogWarning(ex, "Scanning for leftover download files failed");
		}
	}

	private async Task ResumeAsync(List<(OrphanFile Orphan, HistoryEntry? Entry)> resumable) {
		foreach (var (orphan, entry) in resumable) {
			try {
				await operations.ResumeLeftoverAsync(entry!.Uri!, Path.GetDirectoryName(orphan.DataFile)!, Path.GetFileName(orphan.DataFile));
			}
			catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or InvalidOperationException) {
				logger.LogWarning(ex, "Could not resume {File}", orphan.DataFile);
			}
		}
	}

	private async Task DeleteAsync(List<OrphanFile> orphans) {
		if (!await App.Window.ConfirmAsync("Delete leftover files?",
				$"This permanently deletes {orphans.Count} partly downloaded {(orphans.Count == 1 ? "file" : "files")} and their .aria2 control files. Finished downloads are not touched.", "Delete")) {
			return;
		}
		var cleaned = await Task.Run(() => OrphanFiles.Clean(orphans, deleteData: true));
		logger.LogInformation("Deleted {Count} leftover downloads", cleaned);
	}
}
