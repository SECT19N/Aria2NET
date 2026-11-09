using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Notifications;

public enum NotificationKind {
	Completed,
	Failed,
	/// <summary>A torrent has all its data (it starts seeding, or finishes outright when it is set not to seed).</summary>
	TorrentFinished,
}

public sealed record DownloadNotification(NotificationKind Kind, DownloadItem Item);

/// <summary>
/// Turns the stream of poll snapshots into "this just finished / failed" events by remembering each download's previous status.
/// The first snapshot after <see cref="Reset"/> (start, or an engine restart) only records, so nothing already finished is announced again.
/// Not thread-safe: call it from one place at a time.
/// </summary>
public sealed class NotificationDetector {
	private Dictionary<string, DownloadStatus> _seen = [];
	private bool _baseline = true;

	/// <summary>Forget everything; the next snapshot is a new baseline. Call when the engine stops or restarts.</summary>
	public void Reset() {
		_seen = [];
		_baseline = true;
	}

	public IReadOnlyList<DownloadNotification> Observe(PollSnapshot snapshot) {
		var found = new List<DownloadNotification>();
		var next = new Dictionary<string, DownloadStatus>(snapshot.Items.Count);
		foreach (var item in snapshot.Items) {
			// A magnet's metadata placeholder is not something the user downloads; the torrent it leads to is announced instead.
			if (item.IsMetadata) {
				continue;
			}
			next[item.Gid] = item.Status;
			if (_baseline) {
				continue;
			}
			var known = _seen.TryGetValue(item.Gid, out var previous);
			if (Classify(item, known, previous) is { } kind) {
				found.Add(new DownloadNotification(kind, item));
			}
		}
		_seen = next; // dropping gone downloads keeps the map from growing forever
		_baseline = false;
		return found;
	}

	private static NotificationKind? Classify(DownloadItem item, bool known, DownloadStatus previous) {
		switch (item.Status) {
			case DownloadStatus.Error when !known || previous != DownloadStatus.Error:
				return NotificationKind.Failed;
			case DownloadStatus.Seeding when !known || previous is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused:
				return NotificationKind.TorrentFinished;
			case DownloadStatus.Complete when !known || previous != DownloadStatus.Complete:
				if (known && previous == DownloadStatus.Seeding) {
					return null; // already announced when seeding began
				}
				return item.IsTorrent ? NotificationKind.TorrentFinished : NotificationKind.Completed;
			default:
				return null;
		}
	}
}
