using System.Security.Cryptography;
using System.Text;

namespace Aria2NET.Core.Torrents;

/// <summary>One file inside a torrent. <see cref="Index"/> is 1-based, as aria2's <c>select-file</c> expects.</summary>
public sealed record TorrentFileEntry(int Index, string Path, long Length);

/// <summary>What a .torrent file says about itself, read without starting anything.</summary>
public sealed record TorrentMetadata(string Name, string InfoHash, IReadOnlyList<TorrentFileEntry> Files, IReadOnlyList<string> Trackers) {
	public const int MaxFileBytes = 10 * 1024 * 1024;

	public long TotalBytes => Files.Sum(f => f.Length);

	/// <summary>Reads a .torrent. Throws <see cref="FormatException"/> with a user-presentable message when it is not a usable torrent.</summary>
	public static TorrentMetadata Parse(byte[] data) {
		if (data.Length is 0 or > MaxFileBytes) {
			throw new FormatException("This is not a torrent file.");
		}
		Dictionary<string, object> root;
		byte[]? rawInfo;
		try {
			root = Bencode.Decode(data) as Dictionary<string, object> ?? throw new FormatException();
			rawInfo = Bencode.FindRawTopLevelValue(data, "info");
		} catch (FormatException) {
			throw new FormatException("This is not a torrent file.");
		}
		if (rawInfo is null || root["info"] is not Dictionary<string, object> info) {
			throw new FormatException("This torrent file has no content description.");
		}

		var name = Text(info, "name.utf-8") ?? Text(info, "name");
		if (string.IsNullOrWhiteSpace(name)) {
			throw new FormatException("This torrent has no name.");
		}

		var files = new List<TorrentFileEntry>();
		if (info.TryGetValue("files", out var fileList)) {
			if (fileList is not List<object> list || list.Count == 0) {
				throw new FormatException("This torrent lists no files.");
			}
			foreach (var item in list) {
				if (item is not Dictionary<string, object> file || !file.TryGetValue("length", out var len) || len is not long length || length < 0) {
					throw new FormatException("This torrent has a damaged file list.");
				}
				var parts = (file.GetValueOrDefault("path.utf-8") ?? file.GetValueOrDefault("path")) as List<object>;
				var path = parts is { Count: > 0 } ? string.Join('/', parts.Select(p => p is byte[] b ? Encoding.UTF8.GetString(b) : "")) : "";
				if (path.Length == 0) {
					throw new FormatException("This torrent has a file without a name.");
				}
				files.Add(new TorrentFileEntry(files.Count + 1, path, length));
			}
		} else if (info.TryGetValue("length", out var single) && single is long singleLength && singleLength >= 0) {
			files.Add(new TorrentFileEntry(1, name, singleLength));
		} else {
			throw new FormatException("This torrent lists no files.");
		}

		var trackers = new List<string>();
		if (Text(root, "announce") is { Length: > 0 } announce) {
			trackers.Add(announce);
		}
		if (root.TryGetValue("announce-list", out var tiers) && tiers is List<object> tierList) {
			foreach (var tier in tierList.OfType<List<object>>()) {
				trackers.AddRange(tier.OfType<byte[]>().Select(Encoding.UTF8.GetString));
			}
		}

		var hash = Convert.ToHexString(SHA1.HashData(rawInfo)).ToLowerInvariant();
		return new TorrentMetadata(name, hash, files, trackers.Distinct(StringComparer.Ordinal).ToList());
	}

	private static string? Text(Dictionary<string, object> d, string key) =>
		d.TryGetValue(key, out var v) && v is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
}
