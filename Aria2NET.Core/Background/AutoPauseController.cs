using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Background;

public enum AutoPauseAction {
	None,
	Pause,
	Resume,
}

public sealed record AutoPauseDecision(AutoPauseAction Action, IReadOnlyList<DownloadItem> Items) {
	public static AutoPauseDecision Nothing { get; } = new(AutoPauseAction.None, []);
}

/// <summary>
/// Pauses what is running when the machine goes onto battery or a metered connection, and resumes exactly those downloads when it comes
/// back. It acts on the change only, so a download the user starts or resumes by hand afterwards is left alone, and a download the user
/// paused themselves is never resumed by it.
/// </summary>
public sealed class AutoPauseController {
	private HashSet<string> _held = [];
	private bool _paused;

	public AutoPauseDecision Evaluate(bool shouldPause, PollSnapshot snapshot) {
		if (shouldPause && !_paused) {
			_paused = true;
			var running = snapshot.Items.Where(i => i.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Seeding).ToList();
			_held = running.Select(i => i.Gid).ToHashSet();
			return running.Count > 0 ? new AutoPauseDecision(AutoPauseAction.Pause, running) : AutoPauseDecision.Nothing;
		}
		if (!shouldPause && _paused) {
			_paused = false;
			var resume = snapshot.Items.Where(i => i.Status == DownloadStatus.Paused && _held.Contains(i.Gid)).ToList();
			_held = [];
			return resume.Count > 0 ? new AutoPauseDecision(AutoPauseAction.Resume, resume) : AutoPauseDecision.Nothing;
		}
		return AutoPauseDecision.Nothing;
	}
}
