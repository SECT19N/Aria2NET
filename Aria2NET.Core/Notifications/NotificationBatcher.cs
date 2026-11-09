using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Notifications;

/// <summary>One toast: its text, and the download it is about when it is about exactly one.</summary>
public sealed record ToastContent(NotificationKind Kind, string Title, string Body, DownloadItem? Single);

/// <summary>Collects events for a short window so five downloads finishing together make one "5 downloads completed" toast, not five.</summary>
public sealed class NotificationBatcher(TimeSpan window) {
	private const int NamesShown = 3;

	private readonly List<DownloadNotification> _pending = [];
	private DateTimeOffset _first;

	public void Add(IEnumerable<DownloadNotification> events, DateTimeOffset now) {
		foreach (var e in events) {
			if (_pending.Count == 0) {
				_first = now;
			}
			_pending.Add(e);
		}
	}

	/// <summary>The toasts to show now: nothing until the window since the first pending event has passed, then one per kind of event.</summary>
	public IReadOnlyList<ToastContent> TakeDue(DateTimeOffset now) {
		if (_pending.Count == 0 || now - _first < window) {
			return [];
		}
		var toasts = new List<ToastContent>();
		foreach (var kind in Enum.GetValues<NotificationKind>()) {
			var items = _pending.Where(p => p.Kind == kind).Select(p => p.Item).ToList();
			if (items.Count > 0) {
				toasts.Add(Describe(kind, items));
			}
		}
		_pending.Clear();
		return toasts;
	}

	private static ToastContent Describe(NotificationKind kind, List<DownloadItem> items) {
		if (items.Count == 1) {
			var item = items[0];
			return kind switch {
				NotificationKind.Failed => new ToastContent(kind, "Download failed", string.IsNullOrWhiteSpace(item.ErrorMessage) ? item.Name : $"{item.Name}\n{item.ErrorMessage}", item),
				NotificationKind.TorrentFinished => new ToastContent(kind, "Torrent finished", item.Name, item),
				_ => new ToastContent(kind, "Download complete", item.Name, item),
			};
		}
		var names = string.Join(", ", items.Take(NamesShown).Select(i => i.Name)) + (items.Count > NamesShown ? $" and {items.Count - NamesShown} more" : "");
		var title = kind switch {
			NotificationKind.Failed => $"{items.Count} downloads failed",
			NotificationKind.TorrentFinished => $"{items.Count} torrents finished",
			_ => $"{items.Count} downloads completed",
		};
		return new ToastContent(kind, title, names, null);
	}
}
