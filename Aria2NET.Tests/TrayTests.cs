using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Tray;

namespace Aria2NET.Tests;

public class TrayTests {
	private static DownloadItem Item(DownloadStatus status, long speed = 0) =>
		new("g" + Guid.NewGuid().ToString("N")[..6], "file", status, 100, 10, speed, null, null, null);

	private static PollSnapshot Snapshot(long down, long up, params DownloadItem[] items) =>
		new(items, new GlobalStats(down, items.Count(i => i.Status is DownloadStatus.Active or DownloadStatus.Seeding), 0, 0, up));

	[Fact]
	public void An_active_download_shows_speed_and_count() {
		var status = TrayStatus.From(Snapshot(2_500_000, 0, Item(DownloadStatus.Active), Item(DownloadStatus.Active)), EngineState.Running);

		Assert.Equal(TrayState.Downloading, status.State);
		Assert.Contains("2 active", status.Tooltip);
		Assert.Contains("↓", status.Tooltip);
	}

	[Fact]
	public void Nothing_running_and_something_paused_is_the_paused_state() {
		var status = TrayStatus.From(Snapshot(0, 0, Item(DownloadStatus.Paused), Item(DownloadStatus.Complete)), EngineState.Running);

		Assert.Equal(TrayState.Paused, status.State);
		Assert.Contains("1 paused", status.Tooltip);
	}

	[Fact]
	public void Seeding_alone_is_idle_but_shows_the_upload_speed() {
		var status = TrayStatus.From(Snapshot(0, 400_000, Item(DownloadStatus.Seeding)), EngineState.Running);

		Assert.Equal(TrayState.Idle, status.State);
		Assert.Contains("Seeding 1", status.Tooltip);
	}

	[Fact]
	public void An_empty_list_is_idle() {
		Assert.Equal(TrayState.Idle, TrayStatus.From(Snapshot(0, 0), EngineState.Running).State);
		Assert.Equal(TrayState.Idle, TrayStatus.From(null, EngineState.Starting).State);
	}

	[Theory]
	[InlineData(EngineState.Failed)]
	[InlineData(EngineState.Crashed)]
	public void A_dead_engine_is_the_error_state_even_with_an_old_snapshot(EngineState engine) {
		var status = TrayStatus.From(Snapshot(1, 0, Item(DownloadStatus.Active)), engine);

		Assert.Equal(TrayState.Error, status.State);
	}

	[Fact]
	public void The_tooltip_fits_the_127_character_limit_of_the_notification_area() {
		var status = TrayStatus.From(Snapshot(long.MaxValue, 0, Item(DownloadStatus.Active)), EngineState.Running);

		Assert.True(status.Tooltip.Length <= 127);
	}

	[Fact]
	public void Idle_has_no_badge_and_the_other_states_have_one() {
		Assert.Null(TrayBadge.ColorOf(TrayState.Idle));
		Assert.NotNull(TrayBadge.ColorOf(TrayState.Downloading));
		Assert.NotNull(TrayBadge.ColorOf(TrayState.Paused));
		Assert.NotNull(TrayBadge.ColorOf(TrayState.Error));
	}

	[Fact]
	public void The_badge_is_an_opaque_dot_in_the_bottom_right_corner_and_leaves_the_rest_alone() {
		const int size = 16;
		var pixels = new byte[size * size * 4];

		TrayBadge.Paint(pixels, size, (10, 20, 30));

		var centre = (11 * size + 11) * 4;
		Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels[centre..(centre + 4)]);
		Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels[0..4]);
	}

	[Fact]
	public void The_window_behaviour_settings_default_to_a_tray_app_and_survive_a_round_trip() {
		using var temp = new TempDir();
		var file = System.IO.Path.Combine(temp.Path, "settings.json");
		var store = new SettingsStore<AppSettings>(file, AppSettingsJsonContext.Default.AppSettings);

		var fresh = store.Load();
		Assert.True(fresh.CloseToTray);
		Assert.False(fresh.MinimizeToTray);
		Assert.True(fresh.ConfirmExitWithActive);

		fresh.CloseToTray = false;
		fresh.MinimizeToTray = true;
		store.Save(fresh);
		var again = store.Load();
		Assert.False(again.CloseToTray);
		Assert.True(again.MinimizeToTray);
	}
}
