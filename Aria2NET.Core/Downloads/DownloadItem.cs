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
	string? FirstUri) {

	/// <summary>0 to 100; 0 while the size is still unknown.</summary>
	public double Progress => TotalBytes > 0 ? Math.Min(100.0, CompletedBytes * 100.0 / TotalBytes) : 0;

	/// <summary>Remaining time, or null when it cannot be estimated.</summary>
	public TimeSpan? Eta =>
		Status == DownloadStatus.Active && SpeedBytesPerSec > 0 && TotalBytes > CompletedBytes
			? TimeSpan.FromSeconds((double)(TotalBytes - CompletedBytes) / SpeedBytesPerSec)
			: null;

	/// <summary>Parses one aria2 status object (all numbers arrive as strings).</summary>
	public static DownloadItem FromJson(JsonElement e) {
		var gid = Str(e, "gid") ?? "";
		string? firstUri = null;
		string? path = null;
		if (e.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0) {
			var first = files[0];
			path = Str(first, "path");
			if (first.TryGetProperty("uris", out var uris) && uris.ValueKind == JsonValueKind.Array && uris.GetArrayLength() > 0) {
				firstUri = Str(uris[0], "uri");
			}
		}

		var name = NameFromPath(path)
			?? (e.TryGetProperty("bittorrent", out var bt) && bt.TryGetProperty("info", out var info) ? Str(info, "name") : null)
			?? NameFromUri(firstUri)
			?? gid;

		var status = Str(e, "status") switch {
			"active" => DownloadStatus.Active,
			"waiting" => DownloadStatus.Waiting,
			"paused" => DownloadStatus.Paused,
			"error" => DownloadStatus.Error,
			"complete" => DownloadStatus.Complete,
			"removed" => DownloadStatus.Removed,
			_ => DownloadStatus.Waiting,
		};

		return new DownloadItem(
			gid, name, status,
			Num(e, "totalLength"), Num(e, "completedLength"), Num(e, "downloadSpeed"),
			Str(e, "errorMessage"), Str(e, "dir"), firstUri);
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

public sealed record GlobalStats(long DownloadSpeed, int NumActive, int NumWaiting, int NumStopped) {
	public static GlobalStats Empty { get; } = new(0, 0, 0, 0);

	public static GlobalStats FromJson(JsonElement e) {
		long L(string n) => e.TryGetProperty(n, out var v) && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
		return new GlobalStats(L("downloadSpeed"), (int)L("numActive"), (int)L("numWaiting"), (int)L("numStopped"));
	}
}

public sealed record PollSnapshot(IReadOnlyList<DownloadItem> Items, GlobalStats Stats);
