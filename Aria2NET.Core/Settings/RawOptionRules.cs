using System.Text.RegularExpressions;

namespace Aria2NET.Core.Settings;

/// <summary>
/// What may go into the raw aria2 options list. The values end up in aria2's config file and in the RPC, so this is a security
/// boundary: it also guards settings that arrive from an imported file.
/// </summary>
public static partial class RawOptionRules {
	public const int MaxOptions = 100;
	public const int MaxValueLength = 1000;

	[GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")]
	private static partial Regex KeyPattern();

	/// <summary>Options the app owns (connection to the engine, files it writes, session handling) or that can run programs or write arbitrary files.</summary>
	private static readonly HashSet<string> Blocked = new(StringComparer.Ordinal) {
		"enable-rpc", "stop-with-process", "input-file", "save-session", "save-session-interval", "dir", "log", "conf-path", "no-conf",
		"dht-file-path", "dht-file-path6", "daemon", "quiet", "enable-color", "console-log-level", "help", "version", "gid", "pause",
		"server-stat-of", "server-stat-if", "save-cookies", "load-cookies", "netrc-path", "rpc-save-upload-metadata",
		"bt-save-metadata", "bt-load-saved-metadata", "metalink-file", "torrent-file", "bt-external-ip", "out", "checksum",
	};

	/// <summary>Null when the option is allowed; otherwise why not.</summary>
	public static string? Validate(string key, string value) {
		if (!KeyPattern().IsMatch(key)) {
			return $"\"{Shorten(key)}\" is not an aria2 option name (lowercase letters, digits and dashes).";
		}
		if (Blocked.Contains(key) || key.StartsWith("rpc-", StringComparison.Ordinal) || key.StartsWith("on-", StringComparison.Ordinal)) {
			return $"\"{key}\" is managed by Aria2NET and cannot be set here.";
		}
		if (key.Contains("passwd", StringComparison.Ordinal) || key.Contains("password", StringComparison.Ordinal) || key.Contains("secret", StringComparison.Ordinal)
			|| key.EndsWith("-user", StringComparison.Ordinal)) {
			return $"\"{key}\" holds a login; use the proxy settings or the Add dialog instead.";
		}
		if (value.Length > MaxValueLength || value.Any(char.IsControl)) {
			return $"The value of \"{key}\" is too long or contains characters that are not allowed.";
		}
		return null;
	}

	/// <summary>Validates a whole list. Returns the first problem, or null.</summary>
	public static string? Validate(IReadOnlyDictionary<string, string> options) {
		if (options.Count > MaxOptions) {
			return $"At most {MaxOptions} options.";
		}
		foreach (var (key, value) in options) {
			if (Validate(key, value) is { } problem) {
				return problem;
			}
		}
		return null;
	}

	/// <summary>Parses text with one <c>key=value</c> per line (blank lines and lines starting with # are skipped).</summary>
	public static (Dictionary<string, string> Options, string? Error) Parse(string text) {
		var options = new Dictionary<string, string>(StringComparer.Ordinal);
		var number = 0;
		foreach (var raw in text.Split('\n')) {
			number++;
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#')) {
				continue;
			}
			var eq = line.IndexOf('=');
			if (eq <= 0) {
				return (options, $"Line {number}: write it as name=value.");
			}
			var key = line[..eq].Trim();
			var value = line[(eq + 1)..].Trim();
			if (Validate(key, value) is { } problem) {
				return (options, $"Line {number}: {problem}");
			}
			options[key] = value;
		}
		return (options, options.Count > MaxOptions ? $"At most {MaxOptions} options." : null);
	}

	public static string ToText(IReadOnlyDictionary<string, string> options) =>
		string.Join("\r\n", options.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key}={o.Value}"));

	private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "…";
}
