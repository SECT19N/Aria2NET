using System.Text.Json;

namespace Aria2NET.Core.Downloads;

/// <summary>Everything on disk that belongs to one download: its files, <c>.aria2</c> control files and, for a multi-file torrent, its folder.</summary>
public sealed record DownloadFilePlan(IReadOnlyList<string> Files, IReadOnlyList<string> ControlFiles, string? Folder) {
	public static DownloadFilePlan Empty { get; } = new([], [], null);

	/// <summary>
	/// Reads aria2's full status. Only paths inside the download folder are kept, so a hostile torrent cannot point the delete elsewhere.
	/// A multi-file torrent keeps its control file next to its folder (<c>Name.aria2</c>), not next to the first file.
	/// </summary>
	public static DownloadFilePlan FromStatus(JsonElement status) {
		var dir = Str(status, "dir");
		if (string.IsNullOrWhiteSpace(dir)) {
			return Empty;
		}
		var paths = new List<string>();
		if (status.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array) {
			paths.AddRange(files.EnumerateArray().Select(f => Str(f, "path")).Where(p => !string.IsNullOrWhiteSpace(p) && !p!.StartsWith("[METADATA]", StringComparison.Ordinal))!);
		}
		var torrentName = status.TryGetProperty("bittorrent", out var bt) && bt.ValueKind == JsonValueKind.Object
			&& bt.TryGetProperty("info", out var info) ? Str(info, "name") : null;
		return Build(dir, paths, paths.Count > 1 ? torrentName : null);
	}

	/// <param name="folderName">The torrent's name when it has several files, so they live in <c>dir/folderName</c>.</param>
	public static DownloadFilePlan Build(string dir, IEnumerable<string> filePaths, string? folderName) {
		var root = Path.GetFullPath(dir);
		var fileList = filePaths.Select(Path.GetFullPath).Where(p => Inside(root, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var controls = fileList.Select(p => p + ".aria2").ToList();
		string? folder = null;
		if (!string.IsNullOrWhiteSpace(folderName) && folderName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) {
			var candidate = Path.GetFullPath(Path.Combine(root, folderName));
			if (Inside(root, candidate)) {
				folder = candidate;
				controls.Add(candidate + ".aria2");
			}
		}
		return new DownloadFilePlan(fileList, controls, folder);
	}

	private static bool Inside(string root, string path) =>
		path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	private static string? Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
