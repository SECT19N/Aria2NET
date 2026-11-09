using Aria2NET.Core.Downloads;
using Aria2NET.Core.Notifications;
using Aria2NET.Core.Settings;

namespace Aria2NET.Tests;

public class NotificationDetectorTests {
	private static DownloadItem Item(string gid, DownloadStatus status, bool torrent = false, bool metadata = false, string? error = null) =>
		new(gid, "name-" + gid, status, 100, 100, 0, error, @"C:\dl", null, IsTorrent: torrent, IsMetadata: metadata);

	private static PollSnapshot Snap(params DownloadItem[] items) => new(items, GlobalStats.Empty);

	[Fact]
	public void The_first_snapshot_only_records_so_old_results_are_not_announced() {
		var detector = new NotificationDetector();

		Assert.Empty(detector.Observe(Snap(Item("a", DownloadStatus.Complete), Item("b", DownloadStatus.Error))));
		Assert.Empty(detector.Observe(Snap(Item("a", DownloadStatus.Complete), Item("b", DownloadStatus.Error))));
	}

	[Fact]
	public void A_download_that_completes_is_announced_once() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("a", DownloadStatus.Active)));

		var done = detector.Observe(Snap(Item("a", DownloadStatus.Complete)));
		var again = detector.Observe(Snap(Item("a", DownloadStatus.Complete)));

		Assert.Equal(NotificationKind.Completed, Assert.Single(done).Kind);
		Assert.Empty(again);
	}

	[Fact]
	public void A_failure_is_announced_once_and_again_after_a_retry_fails() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("a", DownloadStatus.Active)));

		Assert.Equal(NotificationKind.Failed, Assert.Single(detector.Observe(Snap(Item("a", DownloadStatus.Error)))).Kind);
		Assert.Empty(detector.Observe(Snap(Item("a", DownloadStatus.Error))));
		detector.Observe(Snap(Item("a", DownloadStatus.Active)));
		Assert.Equal(NotificationKind.Failed, Assert.Single(detector.Observe(Snap(Item("a", DownloadStatus.Error)))).Kind);
	}

	[Fact]
	public void A_torrent_is_finished_when_seeding_starts_and_not_again_when_seeding_ends() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("t", DownloadStatus.Active, torrent: true)));

		var seeding = detector.Observe(Snap(Item("t", DownloadStatus.Seeding, torrent: true)));
		var ended = detector.Observe(Snap(Item("t", DownloadStatus.Complete, torrent: true)));

		Assert.Equal(NotificationKind.TorrentFinished, Assert.Single(seeding).Kind);
		Assert.Empty(ended);
	}

	[Fact]
	public void A_torrent_that_does_not_seed_is_finished_when_it_completes() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("t", DownloadStatus.Active, torrent: true)));

		Assert.Equal(NotificationKind.TorrentFinished, Assert.Single(detector.Observe(Snap(Item("t", DownloadStatus.Complete, torrent: true)))).Kind);
	}

	[Fact]
	public void A_magnets_metadata_placeholder_is_never_announced() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("m", DownloadStatus.Active, torrent: true, metadata: true)));

		Assert.Empty(detector.Observe(Snap(Item("m", DownloadStatus.Complete, torrent: true, metadata: true))));
	}

	[Fact]
	public void A_download_that_appears_already_finished_after_the_baseline_is_announced() {
		var detector = new NotificationDetector();
		detector.Observe(Snap());

		Assert.Equal(NotificationKind.Completed, Assert.Single(detector.Observe(Snap(Item("quick", DownloadStatus.Complete)))).Kind);
	}

	[Fact]
	public void After_a_reset_the_next_snapshot_is_a_new_baseline() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("a", DownloadStatus.Active)));
		detector.Reset();

		Assert.Empty(detector.Observe(Snap(Item("a", DownloadStatus.Complete))));
	}

	[Fact]
	public void Pausing_and_removing_make_no_noise() {
		var detector = new NotificationDetector();
		detector.Observe(Snap(Item("a", DownloadStatus.Active)));

		Assert.Empty(detector.Observe(Snap(Item("a", DownloadStatus.Paused))));
		Assert.Empty(detector.Observe(Snap(Item("a", DownloadStatus.Removed))));
	}
}

public class NotificationBatcherTests {
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	private static DownloadNotification Note(NotificationKind kind, string name, string? error = null) =>
		new(kind, new DownloadItem("g" + name, name, DownloadStatus.Complete, 1, 1, 0, error, @"C:\dl", null, Path: @"C:\dl\" + name));

	[Fact]
	public void Nothing_is_shown_until_the_window_has_passed() {
		var batcher = new NotificationBatcher(TimeSpan.FromSeconds(3));
		batcher.Add([Note(NotificationKind.Completed, "a.zip")], T0);

		Assert.Empty(batcher.TakeDue(T0.AddSeconds(2)));
		Assert.Single(batcher.TakeDue(T0.AddSeconds(3)));
		Assert.Empty(batcher.TakeDue(T0.AddSeconds(10)));
	}

	[Fact]
	public void One_event_keeps_its_download_for_the_click_actions() {
		var batcher = new NotificationBatcher(TimeSpan.Zero);
		batcher.Add([Note(NotificationKind.Completed, "a.zip")], T0);

		var toast = Assert.Single(batcher.TakeDue(T0));

		Assert.Equal("Download complete", toast.Title);
		Assert.Equal("a.zip", toast.Body);
		Assert.Equal("a.zip", toast.Single!.Name);
	}

	[Fact]
	public void Several_completions_become_one_summary_toast() {
		var batcher = new NotificationBatcher(TimeSpan.Zero);
		batcher.Add(Enumerable.Range(1, 5).Select(i => Note(NotificationKind.Completed, $"f{i}.zip")), T0);

		var toast = Assert.Single(batcher.TakeDue(T0));

		Assert.Equal("5 downloads completed", toast.Title);
		Assert.Equal("f1.zip, f2.zip, f3.zip and 2 more", toast.Body);
		Assert.Null(toast.Single);
	}

	[Fact]
	public void Each_kind_of_event_gets_its_own_toast() {
		var batcher = new NotificationBatcher(TimeSpan.Zero);
		batcher.Add([Note(NotificationKind.Completed, "a"), Note(NotificationKind.Failed, "b", "Not found"), Note(NotificationKind.TorrentFinished, "c")], T0);

		var toasts = batcher.TakeDue(T0);

		Assert.Equal([NotificationKind.Completed, NotificationKind.Failed, NotificationKind.TorrentFinished], toasts.Select(t => t.Kind));
		Assert.Equal("b\nNot found", toasts[1].Body);
	}

	[Fact]
	public void Events_arriving_later_join_the_same_window() {
		var batcher = new NotificationBatcher(TimeSpan.FromSeconds(3));
		batcher.Add([Note(NotificationKind.Completed, "a")], T0);
		batcher.Add([Note(NotificationKind.Completed, "b")], T0.AddSeconds(2));

		Assert.Equal("2 downloads completed", Assert.Single(batcher.TakeDue(T0.AddSeconds(3))).Title);
	}

	[Fact]
	public void Notifications_default_on_except_the_focus_filter() {
		var s = new AppSettings();

		Assert.True(s.NotifyOnComplete);
		Assert.True(s.NotifyOnFailed);
		Assert.True(s.NotifyOnTorrentFinished);
		Assert.True(s.NotifySound);
		Assert.False(s.NotifyOnlyWhenUnfocused);
	}
}
