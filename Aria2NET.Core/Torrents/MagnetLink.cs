namespace Aria2NET.Core.Torrents;

public static class MagnetLink {
	private const string Prefix = "urn:btih:";

	/// <summary>The torrent's info hash as 40 lowercase hex characters, from a <c>magnet:</c> link (hex or base32 form).</summary>
	public static bool TryGetInfoHash(string magnet, out string infoHash) {
		infoHash = "";
		if (!magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) {
			return false;
		}
		foreach (var part in magnet[8..].Split('&')) {
			var eq = part.IndexOf('=');
			if (eq <= 0 || !part[..eq].Equals("xt", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			var value = Uri.UnescapeDataString(part[(eq + 1)..]);
			if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			var hash = value[Prefix.Length..];
			if (hash.Length == 40 && hash.All(Uri.IsHexDigit)) {
				infoHash = hash.ToLowerInvariant();
				return true;
			}
			if (hash.Length == 32 && TryDecodeBase32(hash, out var bytes)) {
				infoHash = Convert.ToHexString(bytes).ToLowerInvariant();
				return true;
			}
		}
		return false;
	}

	/// <summary>The display name (<c>dn</c>) of a magnet link, if it has one.</summary>
	public static string? DisplayName(string magnet) {
		var query = magnet.Split('?', 2).ElementAtOrDefault(1);
		var dn = query?.Split('&').FirstOrDefault(p => p.StartsWith("dn=", StringComparison.OrdinalIgnoreCase));
		return dn is null ? null : Uri.UnescapeDataString(dn[3..].Replace('+', ' '));
	}

	private static bool TryDecodeBase32(string text, out byte[] bytes) {
		const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		bytes = new byte[text.Length * 5 / 8];
		var buffer = 0;
		var bits = 0;
		var index = 0;
		foreach (var c in text.ToUpperInvariant()) {
			var v = Alphabet.IndexOf(c);
			if (v < 0) {
				return false;
			}
			buffer = (buffer << 5) | v;
			bits += 5;
			if (bits >= 8) {
				bits -= 8;
				bytes[index++] = (byte)(buffer >> bits);
				buffer &= (1 << bits) - 1;
			}
		}
		return true;
	}
}

/// <summary>Builds aria2's <c>select-file</c> value ("1,3-5") from the chosen file indexes.</summary>
public static class SelectFileSpec {
	/// <summary>Null when nothing needs to be said: every file is chosen, so aria2's default (all) applies. Throws when nothing is chosen.</summary>
	public static string? Build(IEnumerable<int> selected, int totalFiles) {
		var sorted = selected.Distinct().Where(i => i >= 1 && i <= totalFiles).Order().ToList();
		if (sorted.Count == 0) {
			throw new ArgumentException("Choose at least one file.", nameof(selected));
		}
		if (sorted.Count == totalFiles) {
			return null;
		}
		var parts = new List<string>();
		for (var i = 0; i < sorted.Count;) {
			var start = sorted[i];
			var end = start;
			while (++i < sorted.Count && sorted[i] == end + 1) {
				end = sorted[i];
			}
			parts.Add(end == start ? $"{start}" : $"{start}-{end}");
		}
		return string.Join(',', parts);
	}
}
