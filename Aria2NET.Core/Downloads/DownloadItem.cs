using System.Globalization;
using System.Text.Json;

namespace Aria2NET.Core.Downloads;

public enum DownloadStatus {
	Active,
	Waiting,
	Paused,
	Error,
	Complete,
	Removed,
	/// <summary>A torrent that has all its data and is uploading (aria2 reports this as active with seeder=true).</summary>
	Seeding,
}

public sealed record DownloadItem(
	string Gid,
	string Name,
	DownloadStatus Status,
	long TotalBytes,
	long CompletedBytes,
	long SpeedBytesPerSec,
	string? ErrorMessage,
	string? Dir,
	string? FirstUri,
	string? Path = null,
	int? ErrorCode = null,
	string? InfoHash = null,
	bool IsTorrent = false,
	long UploadSpeedBytesPerSec = 0,
	long UploadedBytes = 0,
	int NumSeeders = 0,
	int Connections = 0,
	string? FollowedBy = null,
	string? BelongsTo = null,
	bool IsMetadata = false,
	int FileCount = 1) {

	/// <summary>Uploaded divided by downloaded; 0 until something was downloaded.</summary>
	public double ShareRatio => CompletedBytes > 0 ? (double) UploadedBytes / CompletedBytes : 0;

	/// <summary>A magnet whose torrent file is still being fetched: aria2 lists it as a placeholder with a <c>[METADATA]</c> path.</summary>
	public bool IsFetchingMetadata => IsMetadata && Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused;

	/// <summary>0 to 100; 0 while the size is still unknown.</summary>
	public double Progress => TotalBytes > 0 ? Math.Min(100.0, CompletedBytes * 100.0 / TotalBytes) : 0;

	/// <summary>Remaining time, or null when it cannot be estimated.</summary>
	public TimeSpan? Eta =>
		Status == DownloadStatus.Active && SpeedBytesPerSec > 0 && TotalBytes > CompletedBytes
			? TimeSpan.FromSeconds((double) (TotalBytes - CompletedBytes) / SpeedBytesPerSec)
			: null;

	/// <summary>aria2 names the path of a magnet's metadata download this way.</summary>
	private const string MetadataPrefix = "[METADATA]";

	/// <summary>Parses one aria2 status object (all numbers arrive as strings).</summary>
	public static DownloadItem FromJson(JsonElement e) {
		var gid = Str(e, "gid") ?? "";
		string? firstUri = null;
		string? path = null;
		var fileCount = 1;
		if (e.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0) {
			fileCount = files.GetArrayLength();
			var first = files[0];
			path = Str(first, "path");
			if (first.TryGetProperty("uris", out var uris) && uris.ValueKind == JsonValueKind.Array && uris.GetArrayLength() > 0) {
				firstUri = Str(uris[0], "uri");
			}
		}

		var isMetadata = path?.StartsWith(MetadataPrefix, StringComparison.Ordinal) == true;
		var infoHash = Str(e, "infoHash");
		var isTorrent = e.TryGetProperty("bittorrent", out var bt) || !string.IsNullOrEmpty(infoHash);

		// Torrent name wins over the first file: a multi-file torrent's first file is not what the user added.
		var torrentName = bt.ValueKind == JsonValueKind.Object && bt.TryGetProperty("info", out var info) ? Str(info, "name") : null;
		var name = (isMetadata ? NameFromPath(path![MetadataPrefix.Length..]) : null)
			?? torrentName
			?? (isMetadata ? null : NameFromPath(path))
			?? NameFromUri(firstUri)
			?? (isMetadata ? infoHash : null)
			?? gid;

		var status = Str(e, "status") switch {
			"active" => Str(e, "seeder") == "true" ? DownloadStatus.Seeding : DownloadStatus.Active,
			"waiting" => DownloadStatus.Waiting,
			"paused" => DownloadStatus.Paused,
			"error" => DownloadStatus.Error,
			"complete" => DownloadStatus.Complete,
			"removed" => DownloadStatus.Removed,
			_ => DownloadStatus.Waiting,
		};

		// The first element is aria2's own list; a magnet's followedBy names the download that continues after its metadata arrives.
		var followedBy = e.TryGetProperty("followedBy", out var fb) && fb.ValueKind == JsonValueKind.Array && fb.GetArrayLength() > 0 ? fb[0].GetString() : null;

		return new DownloadItem(
			gid, name, status,
			Num(e, "totalLength"), Num(e, "completedLength"), Num(e, "downloadSpeed"),
			Str(e, "errorMessage"), Str(e, "dir"), firstUri,
			string.IsNullOrWhiteSpace(path) || isMetadata ? null : path,
			int.TryParse(Str(e, "errorCode"), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null,
			string.IsNullOrEmpty(infoHash) ? null : infoHash.ToLowerInvariant(),
			isTorrent,
			Num(e, "uploadSpeed"), Num(e, "uploadLength"), (int) Num(e, "numSeeders"), (int) Num(e, "connections"),
			followedBy, Str(e, "belongsTo"), isMetadata, fileCount);
	}

	private static string? NameFromPath(string? path) {
		if (string.IsNullOrWhiteSpace(path)) {
			return null;
		}
		var name = path.Replace('\\', '/');
		name = name[(name.LastIndexOf('/') + 1)..];
		return name.Length > 0 ? name : null;
	}

	private static string? NameFromUri(string? uri) {
		if (string.IsNullOrWhiteSpace(uri)) {
			return null;
		}
		if (uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) {
			var dn = uri.Split('?', 2).ElementAtOrDefault(1)?.Split('&')
				.FirstOrDefault(p => p.StartsWith("dn=", StringComparison.OrdinalIgnoreCase));
			return dn is null ? null : Uri.UnescapeDataString(dn[3..].Replace('+', ' '));
		}
		if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) {
			return null;
		}
		return NameFromPath(Uri.UnescapeDataString(parsed.AbsolutePath));
	}

	private static string? Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static long Num(JsonElement e, string name) =>
		long.TryParse(Str(e, name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

public sealed record GlobalStats(long DownloadSpeed, int NumActive, int NumWaiting, int NumStopped, long UploadSpeed = 0) {
	public static GlobalStats Empty { get; } = new(0, 0, 0, 0);

	public static GlobalStats FromJson(JsonElement e) {
		long L(string n) => e.TryGetProperty(n, out var v) && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
		return new GlobalStats(L("downloadSpeed"), (int) L("numActive"), (int) L("numWaiting"), (int) L("numStopped"), L("uploadSpeed"));
	}
}

public sealed record PollSnapshot(IReadOnlyList<DownloadItem> Items, GlobalStats Stats);
