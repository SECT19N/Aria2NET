using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Torrents;

/// <summary>
/// Public tracker list that is added to every torrent (<c>bt-tracker</c>) so magnets and trackerless torrents find peers faster.
/// Fetched over HTTP, cached in <see cref="AppPaths.TrackerListFile"/>, and only ever read from the cache at engine start so a dead
/// network never delays startup.
/// </summary>
public sealed class TrackerListService(HttpClient http, AppPaths paths, ILogger<TrackerListService>? logger = null) {
	public const string DefaultUrl = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";

	/// <summary>Trackers beyond this are dropped: every one is contacted for every torrent.</summary>
	public const int MaxTrackers = 40;

	private const long MaxDownloadBytes = 512 * 1024;
	private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
	private static readonly string[] Schemes = ["http", "https", "udp"];

	/// <summary>Keeps tracker addresses only. The result is written into aria2's config file, so a line with a comma, space or newline could smuggle in another option and is rejected.</summary>
	public static IReadOnlyList<string> Parse(string? text) {
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var raw in (text ?? "").Split('\n')) {
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#') || line.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ',')) {
				continue;
			}
			if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)
				|| !Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
				|| string.IsNullOrEmpty(uri.Host)
				|| !seen.Add(line)) {
				continue;
			}
			result.Add(line);
			if (result.Count == MaxTrackers) {
				break;
			}
		}
		return result;
	}

	/// <summary>The cached list, or empty when there is none or it cannot be read.</summary>
	public static IReadOnlyList<string> ReadCache(AppPaths paths) {
		try {
			return File.Exists(paths.TrackerListFile) ? Parse(File.ReadAllText(paths.TrackerListFile)) : [];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	/// <summary>True when there is no cached list or it is older than a week.</summary>
	public bool IsStale(DateTime nowUtc) {
		try {
			return !File.Exists(paths.TrackerListFile) || nowUtc - File.GetLastWriteTimeUtc(paths.TrackerListFile) > MaxAge;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return true;
		}
	}

	/// <summary>
	/// Downloads the list and replaces the cache. Returns the new list, or null when nothing changed (download failed, or the answer held
	/// no usable tracker, in which case the old cache is kept).
	/// </summary>
	public async Task<IReadOnlyList<string>?> RefreshAsync(string url = DefaultUrl, CancellationToken ct = default) {
		try {
			using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength > MaxDownloadBytes) {
				logger?.LogWarning("Tracker list at {Url} is too large; ignored", url);
				return null;
			}
			await using var stream = await response.Content.ReadAsStreamAsync(ct);
			using var buffer = new MemoryStream();
			var chunk = new byte[16 * 1024];
			int read;
			while ((read = await stream.ReadAsync(chunk, ct)) > 0) {
				buffer.Write(chunk, 0, read);
				if (buffer.Length > MaxDownloadBytes) {
					logger?.LogWarning("Tracker list at {Url} is too large; ignored", url);
					return null;
				}
			}

			var list = Parse(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
			if (list.Count == 0) {
				logger?.LogWarning("Tracker list at {Url} had no usable trackers; keeping the cached one", url);
				return null;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(paths.TrackerListFile)!);
			var temp = paths.TrackerListFile + ".tmp";
			await File.WriteAllLinesAsync(temp, list, ct);
			File.Move(temp, paths.TrackerListFile, overwrite: true);
			logger?.LogInformation("Tracker list refreshed: {Count} trackers", list.Count);
			return list;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested) {
			logger?.LogWarning("Could not refresh the tracker list: {Message}", ex.Message);
			return null;
		}
	}
}
