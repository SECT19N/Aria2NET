using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Torrents;

public static class TorrentSummary {
	/// <summary>Footer text for the torrents that are running, like "2 seeding · ↑ 1.2 MB/s · 31 peers"; empty when none is.</summary>
	public static string Describe(PollSnapshot snapshot) {
		var live = snapshot.Items.Where(i => i.IsTorrent && !i.IsMetadata && i.Status is DownloadStatus.Active or DownloadStatus.Seeding).ToList();
		if (live.Count == 0) {
			return "";
		}
		var seeding = live.Count(i => i.Status == DownloadStatus.Seeding);
		var parts = new List<string>();
		if (seeding > 0) {
			parts.Add($"{seeding} seeding");
		}
		parts.Add("↑ " + ByteFormat.Speed(snapshot.Stats.UploadSpeed));
		parts.Add($"{live.Sum(i => i.Connections)} peers");
		return string.Join(" · ", parts);
	}
}
