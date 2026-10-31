namespace Aria2NET.Core.Downloads;

public enum StatusFilter {
	All,
	Downloading,
	Waiting,
	Paused,
	Completed,
	Failed,
}

public enum SortColumn {
	/// <summary>aria2's own order (queue order).</summary>
	None,
	Name,
	Size,
	Progress,
	Speed,
	Eta,
	Status,
}

/// <summary>Pure filter/sort logic so it can be tested without any UI.</summary>
public static class DownloadView {
	public static bool Matches(StatusFilter filter, DownloadItem i) => filter switch {
		StatusFilter.All => true,
		StatusFilter.Downloading => i.Status == DownloadStatus.Active,
		StatusFilter.Waiting => i.Status == DownloadStatus.Waiting,
		StatusFilter.Paused => i.Status == DownloadStatus.Paused,
		StatusFilter.Completed => i.Status == DownloadStatus.Complete,
		StatusFilter.Failed => i.Status is DownloadStatus.Error or DownloadStatus.Removed,
		_ => true,
	};

	public static int Count(StatusFilter filter, IEnumerable<DownloadItem> items) => items.Count(i => Matches(filter, i));

	/// <summary>Filters, then sorts. Ties keep aria2's order (the sort is stable).</summary>
	public static List<DownloadItem> Apply(IEnumerable<DownloadItem> items, StatusFilter filter, SortColumn column, bool descending) {
		var filtered = items.Where(i => Matches(filter, i));
		if (column == SortColumn.None) {
			return filtered.ToList();
		}

		IOrderedEnumerable<DownloadItem> ordered = (column, descending) switch {
			(SortColumn.Name, false) => filtered.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
			(SortColumn.Name, true) => filtered.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
			(SortColumn.Size, false) => filtered.OrderBy(i => i.TotalBytes),
			(SortColumn.Size, true) => filtered.OrderByDescending(i => i.TotalBytes),
			(SortColumn.Progress, false) => filtered.OrderBy(i => i.Progress),
			(SortColumn.Progress, true) => filtered.OrderByDescending(i => i.Progress),
			(SortColumn.Speed, false) => filtered.OrderBy(i => i.SpeedBytesPerSec),
			(SortColumn.Speed, true) => filtered.OrderByDescending(i => i.SpeedBytesPerSec),
			// Unknown ETA sorts as "longest".
			(SortColumn.Eta, false) => filtered.OrderBy(i => i.Eta ?? TimeSpan.MaxValue),
			(SortColumn.Eta, true) => filtered.OrderByDescending(i => i.Eta ?? TimeSpan.MaxValue),
			(SortColumn.Status, false) => filtered.OrderBy(i => i.Status),
			_ => filtered.OrderByDescending(i => i.Status),
		};
		return ordered.ToList();
	}
}
