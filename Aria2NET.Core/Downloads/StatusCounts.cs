namespace Aria2NET.Core.Downloads;

/// <summary>The numbers in the window's status bar: overall speeds and how many downloads are in each state.</summary>
public sealed record StatusCounts(long DownloadSpeed, long UploadSpeed, int Active, int Waiting, int Paused, int Seeding, int Completed, int Failed, int Peers) {
	public static StatusCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

	/// <summary>Counts the way the Downloads page's filter tabs do, so the bar and the tabs never disagree.</summary>
	public static StatusCounts From(PollSnapshot snapshot) {
		var items = snapshot.Items.Where(i => !DownloadView.IsSuperseded(i)).ToList();
		return new StatusCounts(
			snapshot.Stats.DownloadSpeed,
			snapshot.Stats.UploadSpeed,
			DownloadView.Count(StatusFilter.Downloading, items),
			DownloadView.Count(StatusFilter.Waiting, items),
			DownloadView.Count(StatusFilter.Paused, items),
			DownloadView.Count(StatusFilter.Seeding, items),
			DownloadView.Count(StatusFilter.Completed, items),
			DownloadView.Count(StatusFilter.Failed, items),
			items.Where(i => i.IsTorrent && !i.IsMetadata && i.Status is DownloadStatus.Active or DownloadStatus.Seeding).Sum(i => i.Connections));
	}
}
