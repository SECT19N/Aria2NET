using System.Globalization;

namespace Aria2NET.Core.Downloads;

/// <summary>Short texts for the second line of a download row.</summary>
public static class RowText {
	/// <summary>Shortens a folder path from the middle so the drive and the last folders stay: <c>E:\…\Games\Repacks</c>.</summary>
	public static string ShortenPath(string? path, int max = 44) {
		if (string.IsNullOrWhiteSpace(path)) {
			return "";
		}
		string trimmed = path.TrimEnd('\\', '/');
		if (trimmed.Length <= max) {
			return trimmed;
		}
		string[] parts = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length <= 2) {
			return Ellipsis(trimmed, max);
		}
		char separator = trimmed.Contains('\\') ? '\\' : '/';
		string first = parts[0];
		foreach (int keep in new[] { 2, 1 }) {
			string tail = string.Join(separator, parts.Skip(parts.Length - keep));
			string candidate = $"{first}{separator}…{separator}{tail}";
			if (candidate.Length <= max) {
				return candidate;
			}
		}
		return Ellipsis($"{first}{separator}…{separator}{parts[^1]}", max);
	}

	private static string Ellipsis(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

	/// <summary>"14 Nov, 21:10", with the year when it is not this year's.</summary>
	public static string When(DateTimeOffset moment, DateTimeOffset now, CultureInfo? culture = null) {
		DateTimeOffset local = moment.ToLocalTime();
		string format = local.Year == now.ToLocalTime().Year ? "d MMM, HH:mm" : "d MMM yyyy, HH:mm";
		return local.ToString(format, culture ?? CultureInfo.CurrentCulture);
	}
}
