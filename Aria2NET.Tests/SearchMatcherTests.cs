using Aria2NET.Core.Search;

namespace Aria2NET.Tests;

public class SearchMatcherTests {
	[Fact]
	public void Splits_a_query_into_words_and_ignores_extra_spaces() {
		Assert.Equal(["ubuntu", "iso"], SearchMatcher.Words("  ubuntu   iso "));
		Assert.Empty(SearchMatcher.Words("   "));
		Assert.Empty(SearchMatcher.Words(null));
	}

	[Fact]
	public void Needs_every_word_but_they_may_sit_in_different_fields() {
		string[] words = SearchMatcher.Words("UBUNTU releases");

		Assert.True(SearchMatcher.Matches(words, "ubuntu-24.04.iso", "https://releases.ubuntu.com/x"));
		Assert.False(SearchMatcher.Matches(words, "ubuntu-24.04.iso", null));
		Assert.False(SearchMatcher.Matches([], "anything"));
	}

	[Theory]
	[InlineData("Download limit (KiB/s)", 0)]
	[InlineData("Alt download limit (KiB/s)", 1)]
	[InlineData("Predownload check", 2)]
	[InlineData("Proxy address", 3)]
	public void Ranks_a_title_by_where_the_first_word_sits(string title, int expected) {
		Assert.Equal(expected, SearchMatcher.Rank(["download"], title));
	}

	[Fact]
	public void Returns_the_best_matches_first_and_no_more_than_the_limit() {
		string[] titles = ["Predownload check", "Alt download limit", "Proxy address", "Download limit", "Downloads at once"];

		List<string> best = SearchMatcher.Best(titles, ["download"], t => t, _ => [], 3);

		Assert.Equal(["Download limit", "Downloads at once", "Alt download limit"], best);
	}

	[Fact]
	public void Finds_an_item_by_a_field_other_than_its_title() {
		(string Name, string Url)[] items = [("file.zip", "https://example.com/a"), ("other.zip", "https://mirror.test/b")];

		List<(string Name, string Url)> best = SearchMatcher.Best(items, ["mirror"], i => i.Name, i => [i.Url], 5);

		Assert.Equal("other.zip", Assert.Single(best).Name);
	}
}
