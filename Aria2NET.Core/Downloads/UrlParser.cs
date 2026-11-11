namespace Aria2NET.Core.Downloads;

public sealed record UrlParseResult(IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid);

/// <summary>What a pasted token is. Local files are told apart by extension only; whether they exist is checked when they are added.</summary>
public enum LinkKind {
	Unsupported,
	/// <summary>http, https, ftp or sftp address.</summary>
	Uri,
	Magnet,
	/// <summary>Full path of a .torrent file on this computer.</summary>
	TorrentFile,
	/// <summary>Full path of a .metalink or .meta4 file on this computer.</summary>
	MetalinkFile,
}

public static class UrlParser {
	private static readonly string[] Schemes = ["http", "https", "ftp", "sftp"];

	/// <summary>Splits pasted text into tokens (whitespace separated), trims, drops duplicates and sorts into valid and invalid.</summary>
	public static UrlParseResult Parse(string? text) {
		var valid = new List<string>();
		var invalid = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var token in Tokens(text)) {
			if (!seen.Add(token)) {
				continue;
			}
			(IsSupported(token) ? valid : invalid).Add(token);
		}
		return new UrlParseResult(valid, invalid);
	}

	public static bool IsSupported(string token) => Classify(token) != LinkKind.Unsupported;

	public static LinkKind Classify(string token) {
		if (token.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) {
			return token.Contains("xt=", StringComparison.OrdinalIgnoreCase) ? LinkKind.Magnet : LinkKind.Unsupported;
		}
		if (Uri.TryCreate(token, UriKind.Absolute, out var uri) && Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)) {
			return string.IsNullOrEmpty(uri.Host) ? LinkKind.Unsupported : LinkKind.Uri;
		}
		if (Path.IsPathFullyQualified(token) && token.IndexOfAny(Path.GetInvalidPathChars()) < 0) {
			return Path.GetExtension(token).ToLowerInvariant() switch {
				".torrent" => LinkKind.TorrentFile,
				".metalink" or ".meta4" => LinkKind.MetalinkFile,
				_ => LinkKind.Unsupported,
			};
		}
		return LinkKind.Unsupported;
	}

	// A path with spaces can be pasted in quotes: "C:\My Files\a.torrent".
	private static IEnumerable<string> Tokens(string? text) {
		foreach (var line in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
			var trimmed = line.Trim();
			if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"') {
				yield return trimmed[1..^1];
				continue;
			}
			foreach (var token in trimmed.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries)) {
				yield return token;
			}
		}
	}
}
