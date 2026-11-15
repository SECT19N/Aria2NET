using System.Globalization;
using Aria2NET.Core.Downloads;

namespace Aria2NET.Tests;

public class RowTextTests {
	[Theory]
	[InlineData(null, "")]
	[InlineData("", "")]
	[InlineData(@"E:\Downloads", @"E:\Downloads")]
	[InlineData(@"E:\Downloads\", @"E:\Downloads")]
	public void Short_paths_are_kept(string? path, string expected) => Assert.Equal(expected, RowText.ShortenPath(path));

	[Fact]
	public void A_long_path_keeps_the_drive_and_the_last_folders() {
		string shortened = RowText.ShortenPath(@"E:\Games\Torrents\Repacks\Racing\Road Rash 30th", 34);

		Assert.Equal(@"E:\…\Racing\Road Rash 30th", shortened);
		Assert.True(shortened.Length <= 34);
	}

	[Fact]
	public void When_even_two_folders_do_not_fit_only_the_last_is_kept_and_then_it_is_cut() {
		Assert.Equal(@"E:\…\Road Rash 30th", RowText.ShortenPath(@"E:\Games\Torrents\Repacks\Racing\Road Rash 30th", 20));
		string cut = RowText.ShortenPath(@"E:\a\b\" + new string('x', 80), 20);
		Assert.Equal(20, cut.Length);
		Assert.EndsWith("…", cut);
	}

	[Fact]
	public void Forward_slashes_are_respected() {
		Assert.Equal("mnt/…/b/c", RowText.ShortenPath("/mnt/aaaaaaaaaa/bbbbbbbbbbbb/b/c", 12));
	}

	[Fact]
	public void The_year_only_shows_when_it_is_not_this_year() {
		CultureInfo culture = CultureInfo.InvariantCulture;
		DateTimeOffset now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
		DateTimeOffset local = new DateTimeOffset(new DateTime(2026, 11, 14, 21, 10, 0, DateTimeKind.Local));
		DateTimeOffset last = new DateTimeOffset(new DateTime(2025, 3, 2, 8, 5, 0, DateTimeKind.Local));

		Assert.Equal("14 Nov, 21:10", RowText.When(local, now, culture));
		Assert.Equal("2 Mar 2025, 08:05", RowText.When(last, now, culture));
	}
}
