using Aria2NET.Core.Downloads;

namespace Aria2NET.Tests;

public class DiskRetryPlannerTests {
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

	private static DownloadItem Failed(string gid, int? code = 17, string? uri = "https://example.com/a.bin") =>
		new(gid, "a.bin", DownloadStatus.Error, 100, 10, 0, "disk", @"E:\Downloads", uri, ErrorCode: code);

	private static PollSnapshot Snap(params DownloadItem[] items) => new(items, GlobalStats.Empty);

	[Theory]
	[InlineData(15, true)]
	[InlineData(16, true)]
	[InlineData(17, true)]
	[InlineData(18, true)]
	[InlineData(9, false)]  // a full disk does not fix itself
	[InlineData(3, false)]  // not found on the server
	[InlineData(6, false)]  // network trouble is aria2's own retry business
	public void Only_file_and_folder_errors_count_as_disk_problems(int code, bool expected) {
		Assert.Equal(expected, DiskRetryPlanner.IsDiskError(Failed("g", code)));
	}

	[Fact]
	public void A_download_that_cannot_be_started_again_is_left_alone() {
		Assert.False(DiskRetryPlanner.IsDiskError(Failed("g", 17, uri: null)));
		Assert.False(DiskRetryPlanner.IsDiskError(Failed("g", 17, uri: "magnet:?xt=urn:btih:abc")));
	}

	[Fact]
	public void Waits_for_the_first_delay_and_then_offers_the_download_once() {
		var planner = new DiskRetryPlanner();
		var snapshot = Snap(Failed("g1"));

		Assert.Empty(planner.Due(snapshot, T0));
		Assert.Empty(planner.Due(snapshot, T0 + DiskRetryPlanner.Delays[0] - TimeSpan.FromSeconds(1)));
		Assert.Single(planner.Due(snapshot, T0 + DiskRetryPlanner.Delays[0]));
		Assert.Empty(planner.Due(snapshot, T0 + TimeSpan.FromMinutes(10))); // already being retried
	}

	[Fact]
	public void The_tries_made_follow_the_download_to_its_new_gid_and_the_waits_grow() {
		var planner = new DiskRetryPlanner();
		var now = T0;
		string gid = "g0";
		for (var attempt = 0; attempt < DiskRetryPlanner.Delays.Count; attempt++) {
			var item = Failed(gid);
			Assert.True(planner.WillRetry(item));
			Assert.Empty(planner.Due(Snap(item), now));
			now += DiskRetryPlanner.Delays[attempt];
			Assert.Single(planner.Due(Snap(item), now));
			var next = "g" + (attempt + 1);
			planner.Started(gid, next, now);
			gid = next;
		}

		var last = Failed(gid);
		Assert.False(planner.WillRetry(last)); // that was the final try
		Assert.Empty(planner.Due(Snap(last), now + TimeSpan.FromHours(1)));
	}

	[Fact]
	public void An_attempt_that_could_not_be_made_still_uses_up_a_try() {
		var planner = new DiskRetryPlanner();
		var item = Failed("g");
		planner.Due(Snap(item), T0);
		Assert.Single(planner.Due(Snap(item), T0 + DiskRetryPlanner.Delays[0]));

		planner.Failed("g");

		var now = T0 + DiskRetryPlanner.Delays[0];
		Assert.Empty(planner.Due(Snap(item), now));
		Assert.Single(planner.Due(Snap(item), now + DiskRetryPlanner.Delays[1]));
	}

	[Fact]
	public void A_released_attempt_does_not_cost_a_try() {
		var planner = new DiskRetryPlanner();
		var item = Failed("g");
		planner.Due(Snap(item), T0);
		Assert.Single(planner.Due(Snap(item), T0 + DiskRetryPlanner.Delays[0]));

		planner.Release("g");

		Assert.Single(planner.Due(Snap(item), T0 + DiskRetryPlanner.Delays[0]));
	}

	[Fact]
	public void Other_failures_are_never_offered() {
		var planner = new DiskRetryPlanner();

		Assert.Empty(planner.Due(Snap(Failed("g", 3)), T0 + TimeSpan.FromHours(1)));
		Assert.False(planner.WillRetry(Failed("g", 3)));
	}
}

public class DiskWakeTests {
	[Fact]
	public async Task Keeps_touching_until_the_disk_answers() {
		var calls = 0;

		var ready = await DiskWake.WaitAsync(@"E:\x", TimeSpan.FromSeconds(30), CancellationToken.None, _ => ++calls >= 3, TimeSpan.FromMilliseconds(1));

		Assert.True(ready);
		Assert.Equal(3, calls);
	}

	[Fact]
	public async Task Gives_up_when_the_disk_never_answers() {
		var ready = await DiskWake.WaitAsync(@"E:\x", TimeSpan.FromMilliseconds(50), CancellationToken.None, _ => false, TimeSpan.FromMilliseconds(10));

		Assert.False(ready);
	}

	[Fact]
	public async Task A_missing_folder_name_counts_as_ready() {
		Assert.True(await DiskWake.WaitAsync(null, TimeSpan.Zero, CancellationToken.None, _ => false));
	}

	[Fact]
	public void Touching_a_real_folder_creates_it_and_leaves_nothing_behind() {
		using var dir = new TempDir();
		var target = Path.Combine(dir.Path, "new", "folder");

		Assert.True(DiskWake.TryTouch(target));

		Assert.True(Directory.Exists(target));
		Assert.Empty(Directory.GetFileSystemEntries(target));
	}

	[Fact]
	public void A_folder_that_cannot_exist_is_not_ready() {
		Assert.False(DiskWake.TryTouch("Q:\\definitely\\not\\here\\" + new string('x', 10) + "\0bad"));
	}
}
