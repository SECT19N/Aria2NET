namespace Aria2NET.Core.Downloads;

/// <summary>Which actions make sense for a download in a given state. One place, so menus, buttons and shortcuts agree.</summary>
public static class DownloadCapabilities {
	public static bool CanPause(DownloadItem i) => i.Status is DownloadStatus.Active or DownloadStatus.Waiting;

	public static bool CanResume(DownloadItem i) => i.Status == DownloadStatus.Paused;

	/// <summary>Retry needs the original address; magnet links and torrents come with 0.5.</summary>
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

	public static bool CanSetSpeedLimit(DownloadItem i) => i.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused;

	public static bool CanOpenFile(DownloadItem i) =>
		i.Status == DownloadStatus.Complete && !string.IsNullOrEmpty(i.Path) && File.Exists(i.Path);

	public static bool CanOpenFolder(DownloadItem i) => !string.IsNullOrEmpty(i.Dir);

	public static bool IsLive(DownloadItem i) => i.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused;
}
