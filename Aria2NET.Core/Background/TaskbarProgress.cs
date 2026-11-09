using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Background;

public enum TaskbarProgressState {
	None,
	/// <summary>Something runs but its size is not known yet.</summary>
	Indeterminate,
	Normal,
	/// <summary>Nothing runs and the rest is paused.</summary>
	Paused,
}

/// <summary>Overall progress for the taskbar button: all unfinished downloads together, so the bar empties again when everything is done.</summary>
public sealed record TaskbarProgress(TaskbarProgressState State, ulong Completed, ulong Total) {
	public static TaskbarProgress None { get; } = new(TaskbarProgressState.None, 0, 0);

	public static TaskbarProgress From(PollSnapshot snapshot) {
		var running = 0;
		var paused = 0;
		ulong total = 0;
		ulong completed = 0;
		foreach (var item in snapshot.Items) {
			switch (item.Status) {
				case DownloadStatus.Active or DownloadStatus.Waiting:
					running++;
					break;
				case DownloadStatus.Paused:
					paused++;
					break;
				default:
					continue;
			}
			total += (ulong)Math.Max(0, item.TotalBytes);
			completed += (ulong)Math.Max(0, Math.Min(item.CompletedBytes, item.TotalBytes));
		}
		if (running + paused == 0) {
			return None;
		}
		if (total == 0) {
			return new TaskbarProgress(running > 0 ? TaskbarProgressState.Indeterminate : TaskbarProgressState.Paused, 0, 0);
		}
		return new TaskbarProgress(running > 0 ? TaskbarProgressState.Normal : TaskbarProgressState.Paused, completed, total);
	}
}
