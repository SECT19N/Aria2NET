using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Search;
using Aria2NET.Pages;
using Aria2NET.ViewModels;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Services;

/// <summary>One suggestion of the title-bar search. <see cref="Target"/> says where choosing it leads.</summary>
public sealed record SearchResult(string Glyph, string Title, string Detail, object? Target);

/// <summary>A download in the Downloads list.</summary>
public sealed record DownloadTarget(string Gid);

/// <summary>An entry on the History page.</summary>
public sealed record HistoryTarget(long Id, string Name);

/// <summary>The title-bar search: looks through the downloads, the history and the settings. Must be used on the UI thread.</summary>
public sealed class GlobalSearch(DownloadsViewModel downloads, HistoryService history, ILogger<GlobalSearch> logger) {
	/// <summary>How many results each of the three groups may contribute.</summary>
	public const int PerGroup = 5;

	public async Task<IReadOnlyList<SearchResult>> FindAsync(string query) {
		string[] words = SearchMatcher.Words(query);
		if (words.Length == 0) {
			return [];
		}
		List<SearchResult> results = new List<SearchResult>();

		IReadOnlyList<DownloadItem> live = downloads.AllItems;
		results.AddRange(SearchMatcher.Best(live, words, i => i.Name, i => [i.FirstUri, i.Path], PerGroup).Select(i => new SearchResult(
			"", i.Name, Loc.F("Search_Download", "Download · {0}", StatusName(i.Status)), new DownloadTarget(i.Gid))));

		if (history.Store is { } store) {
			try {
				HashSet<string> liveGids = live.Select(i => i.Gid).ToHashSet(StringComparer.Ordinal);
				// The database matches one phrase, so it narrows by the first word and the rest is checked here.
				IReadOnlyList<HistoryEntry> entries = await Task.Run(() => store.Query(new HistoryQuery(Search: words[0], Limit: 500)));
				// Everything still in the Downloads list is in the history too; it is already listed above.
				results.AddRange(entries.Where(e => !liveGids.Contains(e.Gid) && SearchMatcher.Matches(words, e.Name, e.Uri, e.Path)).Take(PerGroup).Select(e => new SearchResult(
					"", e.Name, Loc.F("Search_History", "History · {0}", (e.FinishedUtc ?? e.AddedUtc).ToLocalTime().ToString("d")), new HistoryTarget(e.Id, e.Name))));
			} catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException) {
				logger.LogWarning(ex, "Searching the history failed");
			}
		}

		results.AddRange(SearchMatcher.Best(SettingsPage.SearchIndex(), words, t => t.Label ?? t.Section, t => [t.Section], PerGroup).Select(t => new SearchResult(
			"", t.Label ?? t.Section, Loc.F("Search_Setting", "Settings · {0}", t.Section), t)));
		return results;
	}

	private static string StatusName(DownloadStatus status) => status switch {
		DownloadStatus.Active => Loc.S("Status_Downloading", "Downloading"),
		DownloadStatus.Seeding => Loc.S("Status_Seeding", "Seeding"),
		DownloadStatus.Waiting => Loc.S("Status_Queued", "Queued"),
		DownloadStatus.Paused => Loc.S("Status_Paused", "Paused"),
		DownloadStatus.Error => Loc.S("Status_Error", "Error"),
		DownloadStatus.Complete => Loc.S("Status_Complete", "Complete"),
		_ => Loc.S("Status_Removed", "Removed"),
	};
}
