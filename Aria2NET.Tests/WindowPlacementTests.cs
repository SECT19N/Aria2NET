using Aria2NET.Core.Settings;

namespace Aria2NET.Tests;

public class WindowPlacementTests {
	private static readonly ScreenRect Primary = new(0, 0, 1920, 1040);
	private static readonly ScreenRect Right = new(1920, 0, 2560, 1400);
	private static readonly ScreenRect Left = new(-1280, 100, 1280, 900);

	private static WindowPlacement P(int x, int y, int w, int h, bool max = false) => new() { X = x, Y = y, Width = w, Height = h, Maximized = max };

	[Fact]
	public void A_placement_that_fits_is_kept() {
		var fit = WindowPlacementFitter.Fit(P(100, 80, 1200, 800), [Primary, Right])!;
		Assert.Equal((100, 80, 1200, 800), (fit.X, fit.Y, fit.Width, fit.Height));
	}

	[Fact]
	public void Nothing_saved_or_nonsense_gives_null() {
		Assert.Null(WindowPlacementFitter.Fit(null, [Primary]));
		Assert.Null(WindowPlacementFitter.Fit(P(0, 0, 0, 0), [Primary]));
		Assert.Null(WindowPlacementFitter.Fit(P(0, 0, 800, 600), []));
	}

	[Fact]
	public void A_window_on_an_unplugged_monitor_is_centred_on_the_primary_one() {
		var fit = WindowPlacementFitter.Fit(P(3000, 200, 1000, 700), [Primary])!;
		Assert.Equal((460, 170, 1000, 700), (fit.X, fit.Y, fit.Width, fit.Height));
	}

	[Fact]
	public void A_window_on_a_second_monitor_stays_there() {
		var fit = WindowPlacementFitter.Fit(P(2000, 50, 1000, 700), [Primary, Right])!;
		Assert.Equal((2000, 50), (fit.X, fit.Y));
	}

	[Fact]
	public void A_window_on_a_monitor_left_of_the_primary_one_keeps_its_negative_coordinates() {
		var fit = WindowPlacementFitter.Fit(P(-1200, 150, 1000, 700), [Primary, Left])!;
		Assert.Equal((-1200, 150), (fit.X, fit.Y));
	}

	[Fact]
	public void A_window_bigger_than_its_screen_is_shrunk_and_pulled_inside() {
		var fit = WindowPlacementFitter.Fit(P(-300, -200, 5000, 4000), [Primary])!;
		Assert.Equal((0, 0, 1920, 1040), (fit.X, fit.Y, fit.Width, fit.Height));
	}

	[Fact]
	public void A_window_mostly_off_the_edge_is_pulled_back_but_one_barely_on_screen_is_recentred() {
		var partly = WindowPlacementFitter.Fit(P(1500, 100, 1000, 700), [Primary])!;
		Assert.Equal(920, partly.X);

		var barely = WindowPlacementFitter.Fit(P(1900, 100, 1000, 700), [Primary])!;
		Assert.Equal(460, barely.X);
	}

	[Fact]
	public void Tiny_sizes_are_raised_to_the_minimum_and_the_maximized_flag_is_kept() {
		var fit = WindowPlacementFitter.Fit(P(10, 10, 100, 100, max: true), [Primary])!;
		Assert.Equal((WindowPlacementFitter.MinWidth, WindowPlacementFitter.MinHeight, true), (fit.Width, fit.Height, fit.Maximized));
	}

	[Fact]
	public void Settings_without_the_new_fields_load_with_defaults() {
		var json = """{ "SchemaVersion": 1, "StatusFilter": "Paused" }""";
		var settings = System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
		Assert.Equal(0, settings.HistoryRetentionDays);
		Assert.Null(settings.Window);
		Assert.Equal("Paused", settings.StatusFilter);
	}

	[Fact]
	public void Window_placement_round_trips_through_the_settings_file() {
		var settings = new AppSettings { HistoryRetentionDays = 90, Window = P(5, 6, 700, 500, max: true) };
		var json = System.Text.Json.JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
		var back = System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
		Assert.Equal(90, back.HistoryRetentionDays);
		Assert.Equal((5, 6, 700, 500, true), (back.Window!.X, back.Window.Y, back.Window.Width, back.Window.Height, back.Window.Maximized));
	}
}
