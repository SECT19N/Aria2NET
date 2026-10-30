namespace Aria2NET.Core.Downloads;

public sealed record UrlParseResult(IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid);

public static class UrlParser {
	private static readonly string[] Schemes = ["http", "https", "ftp", "sftp"];

	/// <summary>Splits pasted text into tokens (whitespace separated), trims, drops duplicates and sorts into valid and invalid.</summary>
	public static UrlParseResult Parse(string? text) {
		var valid = new List<string>();
		var invalid = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var token in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
			if (!seen.Add(token)) {
				continue;
			}
			(IsSupported(token) ? valid : invalid).Add(token);
		}
		return new UrlParseResult(valid, invalid);
	}

	public static bool IsSupported(string token) {
		if (token.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) {
			return token.Contains("xt=", StringComparison.OrdinalIgnoreCase);
		}
		return Uri.TryCreate(token, UriKind.Absolute, out var uri)
			&& Schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
			&& !string.IsNullOrEmpty(uri.Host);
	}
}
