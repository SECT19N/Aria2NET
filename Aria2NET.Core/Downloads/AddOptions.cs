using System.Text;
using System.Text.RegularExpressions;

namespace Aria2NET.Core.Downloads;

/// <summary>
/// Extra options the user can give one Add (the dialog's "More options"). Mapped to aria2's per-download options; nothing here is
/// ever saved, and <see cref="ToString"/> hides everything so a stray log line cannot leak a password or cookie.
/// </summary>
public sealed partial record AddOptions(
	IReadOnlyList<string>? Headers = null,
	string? Referer = null,
	string? Cookie = null,
	string? HttpUser = null,
	string? HttpPassword = null,
	string? FtpUser = null,
	string? FtpPassword = null,
	string? Checksum = null,
	int MaxDownloadKiBps = 0,
	int MaxUploadKiBps = 0) {

	public static AddOptions None { get; } = new();

	[GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+:[ \t]*\S.*$")]
	private static partial Regex HeaderLine();

	[GeneratedRegex(@"^(sha-1|sha-224|sha-256|sha-384|sha-512|md5|adler32)=([0-9a-fA-F]{2})+$", RegexOptions.IgnoreCase)]
	private static partial Regex ChecksumText();

	private bool PrintMembers(StringBuilder builder) {
		builder.Append("(hidden)");
		return true;
	}

	/// <summary>The header lines to send: the ones typed, then the cookie as a <c>Cookie</c> header.</summary>
	public IReadOnlyList<string> HeaderLines() {
		var lines = (Headers ?? []).Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
		if (!string.IsNullOrWhiteSpace(Cookie)) {
			lines.Add("Cookie: " + Cookie.Trim());
		}
		return lines;
	}

	/// <summary>Null when everything is usable; otherwise a short reason. Control characters are refused everywhere: these values end up in a protocol, and a newline could start another header.</summary>
	public string? Validate() {
		foreach (var line in (Headers ?? []).Select(h => h.Trim()).Where(h => h.Length > 0)) {
			if (HasControl(line) || !HeaderLine().IsMatch(line)) {
				return $"A header must look like \"Name: value\" ({Shorten(line)}).";
			}
		}
		if (!string.IsNullOrWhiteSpace(Referer)) {
			var referer = Referer.Trim();
			if (referer != "*" && (HasControl(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) {
				return "The referer must be a full http:// or https:// address.";
			}
		}
		if (!string.IsNullOrWhiteSpace(Cookie) && HasControl(Cookie)) {
			return "The cookie contains characters that are not allowed.";
		}
		foreach (var text in new[] { HttpUser, HttpPassword, FtpUser, FtpPassword }) {
			if (text is not null && HasControl(text)) {
				return "A user name or password contains characters that are not allowed.";
			}
		}
		if (!string.IsNullOrWhiteSpace(Checksum) && !ChecksumText().IsMatch(Checksum.Trim())) {
			return "The checksum must look like sha-256=<hex digits> (also sha-1, sha-512, md5).";
		}
		if (MaxDownloadKiBps < 0 || MaxUploadKiBps < 0) {
			return "Speed limits cannot be negative.";
		}
		return null;
	}

	/// <summary>aria2 per-download options. The <c>header</c> value holds one header per line; the RPC client turns it into the array aria2 expects.</summary>
	public IReadOnlyDictionary<string, string> ToOptions() {
		var options = new Dictionary<string, string>();
		var headers = HeaderLines();
		if (headers.Count > 0) {
			options["header"] = string.Join('\n', headers);
		}
		if (!string.IsNullOrWhiteSpace(Referer)) {
			options["referer"] = Referer.Trim();
		}
		if (!string.IsNullOrWhiteSpace(HttpUser)) {
			options["http-user"] = HttpUser.Trim();
			if (!string.IsNullOrEmpty(HttpPassword)) {
				options["http-passwd"] = HttpPassword;
			}
		}
		if (!string.IsNullOrWhiteSpace(FtpUser)) {
			options["ftp-user"] = FtpUser.Trim();
			if (!string.IsNullOrEmpty(FtpPassword)) {
				options["ftp-passwd"] = FtpPassword;
			}
		}
		if (!string.IsNullOrWhiteSpace(Checksum)) {
			options["checksum"] = Checksum.Trim();
		}
		if (MaxDownloadKiBps > 0) {
			options["max-download-limit"] = MaxDownloadKiBps + "K";
		}
		if (MaxUploadKiBps > 0) {
			options["max-upload-limit"] = MaxUploadKiBps + "K";
		}
		return options;
	}

	private static bool HasControl(string text) => text.Any(char.IsControl);

	private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
