using System.Globalization;
using System.Text.Json;

namespace Aria2NET.Core.Torrents;

/// <summary>One file of a torrent or metalink download, from <c>aria2.getFiles</c>. <see cref="Index"/> is 1-based, as <c>select-file</c> expects.</summary>
public sealed record DownloadFile(int Index, string Path, long Length, long CompletedLength, bool Selected) {
	public static DownloadFile FromJson(JsonElement e) => new(
		(int) Num(e, "index"), Str(e, "path") ?? "", Num(e, "length"), Num(e, "completedLength"), Str(e, "selected") == "true");

	internal static string? Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	internal static long Num(JsonElement e, string name) =>
		long.TryParse(Str(e, name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

/// <summary>One connected peer, from <c>aria2.getPeers</c>.</summary>
public sealed record PeerInfo(
	string PeerId,
	string Ip,
	int Port,
	string Bitfield,
	bool AmChoking,
	bool PeerChoking,
	long DownloadSpeed,
	long UploadSpeed,
	bool Seeder) {

	public static PeerInfo FromJson(JsonElement e) => new(
		Uri.UnescapeDataString(DownloadFile.Str(e, "peerId") ?? ""),
		DownloadFile.Str(e, "ip") ?? "",
		(int) DownloadFile.Num(e, "port"),
		DownloadFile.Str(e, "bitfield") ?? "",
		DownloadFile.Str(e, "amChoking") == "true",
		DownloadFile.Str(e, "peerChoking") == "true",
		DownloadFile.Num(e, "downloadSpeed"),
		DownloadFile.Num(e, "uploadSpeed"),
		DownloadFile.Str(e, "seeder") == "true");
}
