namespace Aria2NET.Core.Search;

/// <summary>Matching and ordering for the title-bar search. Pure, so it can be tested without any UI.</summary>
public static class SearchMatcher {
	/// <summary>The query split into words; empty when there is nothing to search for.</summary>
	public static string[] Words(string? query) =>
		(query ?? "").Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>True when every word occurs in at least one of the fields, ignoring case. No words match nothing.</summary>
	public static bool Matches(IReadOnlyList<string> words, params string?[] fields) =>
		words.Count > 0 && words.All(w => fields.Any(f => f is not null && f.Contains(w, StringComparison.CurrentCultureIgnoreCase)));

	/// <summary>How well a title fits: 0 when it starts with the first word, 1 when one of its words does, 2 when the word is only somewhere inside, 3 when the title does not hold it at all.</summary>
	public static int Rank(IReadOnlyList<string> words, string title) {
		if (words.Count == 0) {
			return 3;
		}
		string word = words[0];
		int index = title.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
		if (index < 0) {
			return 3;
		}
		if (index == 0) {
			return 0;
		}
		while (index >= 0) {
			if (!char.IsLetterOrDigit(title[index - 1])) {
				return 1;
			}
			index = title.IndexOf(word, index + 1, StringComparison.CurrentCultureIgnoreCase);
		}
		return 2;
	}

	/// <summary>The best <paramref name="limit"/> matches, best first; equally good ones keep the order they came in.</summary>
	public static List<T> Best<T>(IEnumerable<T> items, IReadOnlyList<string> words, Func<T, string> title, Func<T, string?[]> otherFields, int limit) =>
		[.. items.Where(i => Matches(words, [title(i), .. otherFields(i)]))
			.OrderBy(i => Rank(words, title(i)))
			.Take(limit)];
}
