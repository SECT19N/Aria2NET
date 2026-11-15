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

		string? name = Text(info, "name.utf-8") ?? Text(info, "name");
		if (string.IsNullOrWhiteSpace(name)) {
			throw new FormatException("This torrent has no name.");
		}

		List<TorrentFileEntry> files = [];
		if (info.TryGetValue("files", out object? fileList)) {
			if (fileList is not List<object> list || list.Count == 0) {
				throw new FormatException("This torrent lists no files.");
			}
			foreach (object item in list) {
				if (item is not Dictionary<string, object> file || !file.TryGetValue("length", out object? len) || len is not long length || length < 0) {
					throw new FormatException("This torrent has a damaged file list.");
				}
				List<object>? parts = (file.GetValueOrDefault("path.utf-8") ?? file.GetValueOrDefault("path")) as List<object>;
				string path = parts is { Count: > 0 } ? string.Join('/', parts.Select(p => p is byte[] b ? Encoding.UTF8.GetString(b) : "")) : "";
				if (path.Length == 0) {
					throw new FormatException("This torrent has a file without a name.");
				}
				files.Add(new TorrentFileEntry(files.Count + 1, path, length));
			}
		} else if (info.TryGetValue("length", out object? single) && single is long singleLength && singleLength >= 0) {
			files.Add(new TorrentFileEntry(1, name, singleLength));
		} else {
			throw new FormatException("This torrent lists no files.");
		}

		List<string> trackers = [];
		if (Text(root, "announce") is { Length: > 0 } announce) {
			trackers.Add(announce);
		}
		if (root.TryGetValue("announce-list", out object? tiers) && tiers is List<object> tierList) {
			foreach (List<object> tier in tierList.OfType<List<object>>()) {
				trackers.AddRange(tier.OfType<byte[]>().Select(Encoding.UTF8.GetString));
			}
		}

		string hash = Convert.ToHexString(SHA1.HashData(rawInfo)).ToLowerInvariant();
		return new TorrentMetadata(name, hash, files, [.. trackers.Distinct(StringComparer.Ordinal)]);
	}

	private static string? Text(Dictionary<string, object> d, string key) =>
		d.TryGetValue(key, out object? v) && v is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
}
