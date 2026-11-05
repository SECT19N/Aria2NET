using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.History;

/// <summary>Turns the monitor's once-a-second snapshots into history rows, writing only when something changed.</summary>
public sealed class HistoryRecorder(HistoryStore store, ILogger? logger = null, Func<DateTime>? clock = null) {
	private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Seen> _seen = [];
	private bool _reconcileNext = true;

	private readonly record struct Seen(DownloadStatus Status, string Name, string? Path, long TotalBytes, int? ErrorCode) {
		public static Seen Of(DownloadItem item) => new(item.Status, item.Name, item.Path, item.TotalBytes, item.ErrorCode);
	}

	/// <summary>Called whenever the engine (re)starts: aria2 may know different downloads now, so the next snapshot is reconciled.</summary>
	public void EngineStarted() {
		lock (_gate) {
			_seen.Clear();
			_reconcileNext = true;
		}
	}

	/// <summary>Never throws: a broken history must not stop the live list.</summary>
	public void OnSnapshot(PollSnapshot snapshot) {
		try {
			lock (_gate) {
				Record(snapshot.Items);
			}
		}
		catch (Exception ex) {
			logger?.LogError(ex, "Recording history failed");
		}
	}

	private void Record(IReadOnlyList<DownloadItem> items) {
		var now = _clock();
		if (_reconcileNext) {
			var result = store.Reconcile(items, now);
			_reconcileNext = false;
			if (result.Interrupted > 0 || result.Relinked > 0) {
				logger?.LogInformation("History reconciled: {Interrupted} interrupted, {Relinked} relinked", result.Interrupted, result.Relinked);
			}
			foreach (var item in items) {
				_seen[item.Gid] = Seen.Of(item);
			}
			return;
		}

		var current = items.Select(i => i.Gid).ToHashSet(StringComparer.Ordinal);
		var vanished = _seen.Where(p => !current.Contains(p.Key) && p.Value.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused)
			.Select(p => p.Key).ToList();
		var changed = items.Where(i => !_seen.TryGetValue(i.Gid, out var seen) || seen != Seen.Of(i)).ToList();

		if (vanished.Count > 0) {
			store.MarkRemoved(vanished, now);
		}
		if (changed.Count > 0) {
			store.Upsert(changed, now);
		}
		foreach (var gid in _seen.Keys.Where(g => !current.Contains(g)).ToList()) {
			_seen.Remove(gid);
		}
		foreach (var item in changed) {
			_seen[item.Gid] = Seen.Of(item);
		}
	}
}
