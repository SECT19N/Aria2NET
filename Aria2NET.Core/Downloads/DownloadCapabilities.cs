namespace Aria2NET.Core.Downloads;

/// <summary>Which actions make sense for a download in a given state. One place, so menus, buttons and shortcuts agree.</summary>
public static class DownloadCapabilities {
	public static bool CanPause(DownloadItem i) => i.Status is DownloadStatus.Active or DownloadStatus.Seeding or DownloadStatus.Waiting;

	public static bool CanResume(DownloadItem i) => i.Status == DownloadStatus.Paused;

	/// <summary>Retry needs the original address; a torrent has none (aria2 does not report it), so it cannot be retried this way.</summary>
	public static bool CanRetry(DownloadItem i) =>
		i.Status is DownloadStatus.Error or DownloadStatus.Removed
		&& !string.IsNullOrEmpty(i.FirstUri)
		&& !i.FirstUri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

	public static bool CanRemove(DownloadItem i) => true;

	public static bool CanForceStart(DownloadItem i) => i.Status == DownloadStatus.Waiting;

	public static bool CanMove(DownloadItem i) => i.Status is DownloadStatus.Waiting or DownloadStatus.Paused;

	/// <summary>Only before any data exists: changing the name or folder later would orphan the partial file.</summary>
	public static bool CanRename(DownloadItem i) => CanMove(i) && i.CompletedBytes == 0;

	public static bool CanChangeFolder(DownloadItem i) => CanRename(i);

	public static bool CanSetSpeedLimit(DownloadItem i) => IsLive(i);

	public static bool CanOpenFile(DownloadItem i) =>
		i.Status is DownloadStatus.Complete or DownloadStatus.Seeding && !string.IsNullOrEmpty(i.Path) && !(i.IsTorrent && i.FileCount > 1) && File.Exists(i.Path);

	public static bool CanOpenFolder(DownloadItem i) => !string.IsNullOrEmpty(i.Dir);

	public static bool IsLive(DownloadItem i) => i.Status is DownloadStatus.Active or DownloadStatus.Seeding or DownloadStatus.Waiting or DownloadStatus.Paused;

	/// <summary>Verify the files against the torrent and repair them. Not for a magnet that has no files yet.</summary>
	public static bool CanRecheck(DownloadItem i) => i.IsTorrent && !i.IsMetadata && IsLive(i);

	/// <summary>Seeding rules apply to a torrent that is still running (finished-and-gone ones have nothing to change).</summary>
	public static bool CanChangeSeeding(DownloadItem i) => CanRecheck(i);
}
