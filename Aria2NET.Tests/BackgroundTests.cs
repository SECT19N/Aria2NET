using Aria2NET.Core.Background;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;

namespace Aria2NET.Tests;

internal static class Snap {
	public static DownloadItem Item(string gid, DownloadStatus status, long total = 100, long done = 0) =>
		new(gid, "n" + gid, status, total, done, 0, null, null, null);

	public static PollSnapshot Of(params DownloadItem[] items) => new(items, GlobalStats.Empty);
}

public class AutoPauseControllerTests {
	[Fact]
	public void Going_onto_battery_pauses_what_runs_and_plugging_in_resumes_exactly_that() {
		var controller = new AutoPauseController();
		var running = Snap.Of(Snap.Item("a", DownloadStatus.Active), Snap.Item("b", DownloadStatus.Waiting), Snap.Item("mine", DownloadStatus.Paused));

		var pause = controller.Evaluate(true, running);
		Assert.Equal(AutoPauseAction.Pause, pause.Action);
		Assert.Equal(["a", "b"], pause.Items.Select(i => i.Gid));

		var after = Snap.Of(Snap.Item("a", DownloadStatus.Paused), Snap.Item("b", DownloadStatus.Paused), Snap.Item("mine", DownloadStatus.Paused));
		var resume = controller.Evaluate(false, after);
		Assert.Equal(AutoPauseAction.Resume, resume.Action);
		Assert.Equal(["a", "b"], resume.Items.Select(i => i.Gid)); // the one the user paused stays paused
	}

	[Fact]
	public void It_acts_on_the_change_only() {
		var controller = new AutoPauseController();
		var running = Snap.Of(Snap.Item("a", DownloadStatus.Active));

		Assert.Equal(AutoPauseAction.None, controller.Evaluate(false, running).Action);
		Assert.Equal(AutoPauseAction.Pause, controller.Evaluate(true, running).Action);
		Assert.Equal(AutoPauseAction.None, controller.Evaluate(true, running).Action); // a download started by hand is left alone
	}

	[Fact]
	public void A_download_the_user_resumed_meanwhile_is_not_touched_on_the_way_back() {
		var controller = new AutoPauseController();
		controller.Evaluate(true, Snap.Of(Snap.Item("a", DownloadStatus.Active)));

		var decision = controller.Evaluate(false, Snap.Of(Snap.Item("a", DownloadStatus.Active)));

		Assert.Equal(AutoPauseAction.None, decision.Action);
	}

	[Fact]
	public void Nothing_running_means_nothing_to_pause_or_resume() {
		var controller = new AutoPauseController();

		Assert.Equal(AutoPauseAction.None, controller.Evaluate(true, Snap.Of()).Action);
		Assert.Equal(AutoPauseAction.None, controller.Evaluate(false, Snap.Of(Snap.Item("late", DownloadStatus.Paused))).Action);
	}
}

public class TaskbarProgressTests {
	[Fact]
	public void Progress_adds_up_the_unfinished_downloads_only() {
		var progress = TaskbarProgress.From(Snap.Of(
			Snap.Item("a", DownloadStatus.Active, 100, 50),
			Snap.Item("b", DownloadStatus.Waiting, 300, 0),
			Snap.Item("done", DownloadStatus.Complete, 1000, 1000)));

		Assert.Equal(new TaskbarProgress(TaskbarProgressState.Normal, 50, 400), progress);
	}

	[Fact]
	public void Nothing_unfinished_hides_the_bar() {
		Assert.Equal(TaskbarProgressState.None, TaskbarProgress.From(Snap.Of(Snap.Item("done", DownloadStatus.Complete))).State);
		Assert.Equal(TaskbarProgressState.None, TaskbarProgress.From(Snap.Of(Snap.Item("seed", DownloadStatus.Seeding))).State);
	}

	[Fact]
	public void An_unknown_size_is_indeterminate_and_only_paused_work_is_paused() {
		Assert.Equal(TaskbarProgressState.Indeterminate, TaskbarProgress.From(Snap.Of(Snap.Item("a", DownloadStatus.Active, 0, 0))).State);
		Assert.Equal(TaskbarProgressState.Paused, TaskbarProgress.From(Snap.Of(Snap.Item("a", DownloadStatus.Paused, 100, 40))).State);
	}
}

public class WhenFinishedMachineTests {
	[Fact]
	public void It_fires_once_when_the_work_that_was_running_has_finished_and_then_disarms() {
		var machine = new WhenFinishedMachine();
		machine.Arm(FinishAction.Shutdown);

		Assert.Equal(FinishAction.None, machine.Observe(Snap.Of(Snap.Item("a", DownloadStatus.Active))));
		Assert.Equal(FinishAction.Shutdown, machine.Observe(Snap.Of(Snap.Item("a", DownloadStatus.Complete))));
		Assert.Equal(FinishAction.None, machine.Observe(Snap.Of(Snap.Item("a", DownloadStatus.Complete))));
		Assert.Equal(FinishAction.None, machine.Action);
	}

	[Fact]
	public void Arming_with_nothing_running_waits_for_the_next_downloads() {
		var machine = new WhenFinishedMachine();
		machine.Arm(FinishAction.Sleep);

		Assert.Equal(FinishAction.None, machine.Observe(Snap.Of()));
		Assert.Equal(FinishAction.None, machine.Observe(Snap.Of(Snap.Item("old", DownloadStatus.Complete))));
		machine.Observe(Snap.Of(Snap.Item("new", DownloadStatus.Active)));
		Assert.Equal(FinishAction.Sleep, machine.Observe(Snap.Of(Snap.Item("new", DownloadStatus.Complete))));
	}

	[Fact]
	public void Paused_downloads_are_not_finished() {
		var machine = new WhenFinishedMachine();
		machine.Arm(FinishAction.Exit);
		machine.Observe(Snap.Of(Snap.Item("a", DownloadStatus.Active)));

		Assert.Equal(FinishAction.None, machine.Observe(Snap.Of(Snap.Item("a", DownloadStatus.Paused))));
	}

	[Fact]
	public void Seeding_does_not_hold_it_back() {
		var machine = new WhenFinishedMachine();
		machine.Arm(FinishAction.Exit);
		machine.Observe(Snap.Of(Snap.Item("t", DownloadStatus.Active)));

		Assert.Equal(FinishAction.Exit, machine.Observe(Snap.Of(Snap.Item("t", DownloadStatus.Seeding))));
	}

	[Fact]
	public void Disarming_cancels_a_pending_action() {
		var machine = new WhenFinishedMachine();
		machine.Arm(FinishAction.Shutdown);
		machine.Observe(Snap.Of(Snap.Item("a", DownloadStatus.Active)));
		machine.Arm(FinishAction.None);

		Assert.Equal(FinishAction.None, machine.Observe(Snap.Of()));
	}

	[Fact]
	public void The_power_and_restart_settings_have_safe_defaults() {
		var s = new AppSettings();

		Assert.True(s.PreventSleep);
		Assert.False(s.PauseOnBattery);
		Assert.False(s.PauseOnMetered);
		Assert.False(s.HotkeysEnabled);
		Assert.True(s.RestartAfterCrash);
	}
}
