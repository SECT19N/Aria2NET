using System.Globalization;
using System.Text.Json;
using Aria2NET.Core.Aria2;

namespace Aria2NET.Core.Torrents;

/// <summary>Names the BitTorrent client behind a peer id (Azureus-style <c>-XX1234-</c> ids and a few older conventions).</summary>
public static class PeerIdClient {
	private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal) {
		["AZ"] = "Vuze",
		["BC"] = "BitComet",
		["BI"] = "BiglyBT",
		["BT"] = "BitTorrent",
		["DE"] = "Deluge",
		["FD"] = "Free Download Manager",
		["KT"] = "KTorrent",
		["LT"] = "libtorrent",
		["lt"] = "libTorrent",
		["qB"] = "qBittorrent",
		["TR"] = "Transmission",
		["UT"] = "µTorrent",
		["UM"] = "µTorrent Mac",
		["UW"] = "µTorrent Web",
		["WW"] = "WebTorrent",
		["WD"] = "WebTorrent Desktop",
		["XL"] = "Xunlei",
		["AR"] = "Arctic",
		["BW"] = "BitWombat",
		["LW"] = "LimeWire",
		["MP"] = "MooPolice",
		["SD"] = "Xunlei",
		["SZ"] = "Shareaza",
		["TL"] = "Tribler",
		["TT"] = "TuoTu",
		["PD"] = "Pando",
		["RT"] = "Retriever",
		["S3"] = "Amazon S3",
		["SP"] = "BitSpirit",
		["ST"] = "SymTorrent",
		["XT"] = "XanTorrent",
		["lp"] = "Lphant",
		["mp"] = "MooPolice",
		["NX"] = "Net Transport",
		["OS"] = "OneSwarm",
		["PT"] = "Popcorn Time",
		["tT"] = "tTorrent",
		["A2"] = "aria2",
	};

	public static string Name(string? peerId) {
		if (string.IsNullOrEmpty(peerId)) {
			return "Unknown";
		}
		if (peerId.StartsWith("aria2", StringComparison.OrdinalIgnoreCase) || peerId.StartsWith("-aria2", StringComparison.OrdinalIgnoreCase)) {
			return "aria2";
		}
		if (peerId.Length >= 8 && peerId[0] == '-' && peerId[7] == '-' && Known.TryGetValue(peerId.Substring(1, 2), out var name)) {
			var version = peerId.Substring(3, 4);
			return version.All(char.IsAsciiLetterOrDigit) ? $"{name} {FormatVersion(version)}" : name;
		}
		return peerId.StartsWith("M", StringComparison.Ordinal) && peerId.Length > 7 && peerId[2] == '-' ? "BitTorrent (old)" : "Unknown";
	}

	// "4650" -> "4.6.5.0" is how most clients encode it; trailing zeros are noise.
	private static string FormatVersion(string digits) => string.Join('.', digits.TrimEnd('0').Select(c => c.ToString()).DefaultIfEmpty("0"));
}

/// <summary>aria2 reports which pieces a download (or a peer) has as a hex string, one bit per piece, highest bit first.</summary>
public static class Bitfield {
	public static bool[] Decode(string? hex, int pieceCount) {
		var bits = new bool[Math.Max(pieceCount, 0)];
		if (string.IsNullOrEmpty(hex)) {
			return bits;
		}
		for (var i = 0; i < bits.Length; i++) {
			var nibbleIndex = i / 4;
			if (nibbleIndex >= hex.Length) {
				break;
			}
			var nibble = Uri.IsHexDigit(hex[nibbleIndex]) ? Convert.ToInt32(hex[nibbleIndex].ToString(), 16) : 0;
			bits[i] = (nibble & (8 >> (i % 4))) != 0;
		}
		return bits;
	}

	public static int Count(string? hex, int pieceCount) => Decode(hex, pieceCount).Count(b => b);

	/// <summary>
	/// Squeezes per-piece values into <paramref name="buckets"/> averages between 0 and 1, so a torrent of any size fits a bar of fixed width.
	/// </summary>
	public static double[] Downsample(IReadOnlyList<double> perPiece, int buckets) {
		var result = new double[Math.Max(buckets, 0)];
		if (perPiece.Count == 0 || result.Length == 0) {
			return result;
		}
		for (var b = 0; b < result.Length; b++) {
			var from = (int) ((long) b * perPiece.Count / result.Length);
			var to = Math.Max(from + 1, (int) ((long) (b + 1) * perPiece.Count / result.Length));
			to = Math.Min(to, perPiece.Count);
			var sum = 0.0;
			for (var i = from; i < to; i++) {
				sum += perPiece[i];
			}
			result[b] = sum / (to - from);
		}
		return result;
	}
}

public sealed record TorrentPeer(string Address, string Client, string? Country, long DownloadSpeed, long UploadSpeed, double Progress, bool IsSeeder, bool ChokesUs);

public sealed record TorrentTracker(string Url, int Tier);

/// <summary>
/// Everything the torrent details window shows, read in one go. <see cref="HaveBar"/> is how much of each part of the torrent we have;
/// <see cref="AvailabilityBar"/> is how many of the connected peers have each part (1 = every peer; 0 with peers connected = nobody has it).
/// </summary>
public sealed record TorrentDetailSnapshot(
	string Name,
	string InfoHash,
	string Status,
	string Directory,
	string? Comment,
	DateTimeOffset? CreatedAt,
	long TotalBytes,
	long CompletedBytes,
	long UploadedBytes,
	long DownloadSpeed,
	long UploadSpeed,
	int SeedersInSwarm,
	int Connections,
	int PieceCount,
	long PieceLength,
	IReadOnlyList<DownloadFile> Files,
	IReadOnlyList<TorrentPeer> Peers,
	IReadOnlyList<TorrentTracker> Trackers,
	double[] HaveBar,
	double[] AvailabilityBar) {

	public double Ratio => CompletedBytes > 0 ? (double) UploadedBytes / CompletedBytes : 0;
	public double Progress => TotalBytes > 0 ? Math.Min(100.0, CompletedBytes * 100.0 / TotalBytes) : 0;
}

public static class TorrentDetailLoader {
	public const int BarBuckets = 240;

	public static async Task<TorrentDetailSnapshot> LoadAsync(Aria2RpcClient rpc, string gid, GeoIpResolver geo, CancellationToken ct = default) {
		using var statusDoc = await rpc.TellStatusFullAsync(gid, ct);
		var status = statusDoc.RootElement;
		var files = await rpc.GetFilesAsync(gid, ct);
		var peers = await rpc.GetPeersAsync(gid, ct);
		return Build(status, files, peers, geo);
	}

	public static TorrentDetailSnapshot Build(JsonElement status, IReadOnlyList<DownloadFile> files, IReadOnlyList<PeerInfo> peers, GeoIpResolver geo) {
		var pieceCount = (int) Num(status, "numPieces");
		var have = Bitfield.Decode(Str(status, "bitfield"), pieceCount);

		var availability = new double[pieceCount];
		foreach (var peer in peers) {
			var theirs = Bitfield.Decode(peer.Bitfield, pieceCount);
			for (var i = 0; i < pieceCount; i++) {
				if (theirs[i]) {
					availability[i] += 1.0 / peers.Count;
				}
			}
		}

		string? comment = null;
		DateTimeOffset? created = null;
		var trackers = new List<TorrentTracker>();
		if (status.TryGetProperty("bittorrent", out var bt) && bt.ValueKind == JsonValueKind.Object) {
			comment = Str(bt, "comment");
			if (long.TryParse(bt.TryGetProperty("creationDate", out var cd) ? cd.ToString() : null, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0) {
				created = DateTimeOffset.FromUnixTimeSeconds(seconds);
			}
			if (bt.TryGetProperty("announceList", out var tiers) && tiers.ValueKind == JsonValueKind.Array) {
				var tier = 0;
				foreach (var group in tiers.EnumerateArray()) {
					tier++;
					var urls = group.ValueKind == JsonValueKind.Array ? group.EnumerateArray().Select(u => u.GetString()) : [group.GetString()];
					trackers.AddRange(urls.Where(u => !string.IsNullOrEmpty(u)).Select(u => new TorrentTracker(u!, tier)));
				}
			}
		}

		var name = bt.ValueKind == JsonValueKind.Object && bt.TryGetProperty("info", out var info) ? Str(info, "name") : null;
		var total = Num(status, "totalLength");
		var completed = Num(status, "completedLength");

		return new TorrentDetailSnapshot(
			name ?? Str(status, "gid") ?? "",
			(Str(status, "infoHash") ?? "").ToLowerInvariant(),
			Str(status, "seeder") == "true" ? "seeding" : Str(status, "status") ?? "",
			Str(status, "dir") ?? "",
			comment, created,
			total, completed, Num(status, "uploadLength"),
			Num(status, "downloadSpeed"), Num(status, "uploadSpeed"),
			(int) Num(status, "numSeeders"), (int) Num(status, "connections"),
			pieceCount, Num(status, "pieceLength"),
			files,
			[.. peers.Select(p => new TorrentPeer(
				p.Ip.Contains(':') ? $"[{p.Ip}]:{p.Port}" : $"{p.Ip}:{p.Port}",
				PeerIdClient.Name(p.PeerId), geo.Lookup(p.Ip), p.DownloadSpeed, p.UploadSpeed,
				pieceCount > 0 ? Bitfield.Count(p.Bitfield, pieceCount) * 100.0 / pieceCount : 0, p.Seeder, p.PeerChoking))],
			trackers,
			Bitfield.Downsample([.. have.Select(b => b ? 1.0 : 0.0)], BarBuckets),
			Bitfield.Downsample(availability, BarBuckets));
	}

	private static string? Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static long Num(JsonElement e, string name) =>
		long.TryParse(Str(e, name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
