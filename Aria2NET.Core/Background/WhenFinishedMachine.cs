using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Background;

public enum FinishAction {
	None,
	Sleep,
	Hibernate,
	Shutdown,
	Exit,
}

/// <summary>
/// "When downloads finish: ...". Armed by choosing an action; it fires once, when everything that was running has finished, and then
/// disarms itself so a sleep or shutdown is never repeated by surprise. Paused downloads count as unfinished, and so does nothing
/// having run yet: arming it with an empty list waits for the next downloads.
/// </summary>
public sealed class WhenFinishedMachine {
	private bool _sawWork;

	public FinishAction Action { get; private set; }

	public static string Label(FinishAction action) => action switch {
		FinishAction.Sleep => "Sleep",
		FinishAction.Hibernate => "Hibernate",
		FinishAction.Shutdown => "Shut down",
		FinishAction.Exit => "Exit Aria2NET",
		_ => "Do nothing",
	};

	public void Arm(FinishAction action) {
		Action = action;
		_sawWork = false;
	}

	/// <summary>The action to carry out now, or <see cref="FinishAction.None"/>.</summary>
	public FinishAction Observe(PollSnapshot snapshot) {
		if (Action == FinishAction.None) {
			return FinishAction.None;
		}
		if (snapshot.Items.Any(i => i.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused)) {
			_sawWork = true;
			return FinishAction.None;
		}
		if (!_sawWork) {
			return FinishAction.None;
		}
		var fire = Action;
		Arm(FinishAction.None);
		return fire;
	}
}
