using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aria2NET.Core.History;

public static class HistoryExport {
	private static readonly string[] Header = ["Name", "Status", "Size (bytes)", "URL", "Folder", "Path", "Category", "Added (UTC)", "Finished (UTC)", "Error"];

	public static string ToCsv(IEnumerable<HistoryEntry> entries) {
		var sb = new StringBuilder();
		sb.AppendJoin(',', Header.Select(Quote)).Append("\r\n");
		foreach (var e in entries) {
			sb.AppendJoin(',', new[] {
				e.Name, e.Status, e.TotalBytes.ToString(CultureInfo.InvariantCulture), e.Uri, e.Dir, e.Path, e.Category,
				Iso(e.AddedUtc), e.FinishedUtc is { } f ? Iso(f) : null, e.ErrorMessage,
			}.Select(Quote)).Append("\r\n");
		}
		return sb.ToString();
	}

	public static string ToJson(IEnumerable<HistoryEntry> entries) {
		using var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) {
			w.WriteStartArray();
			foreach (var e in entries) {
				w.WriteStartObject();
				w.WriteString("name", e.Name);
				w.WriteString("status", e.Status);
				w.WriteNumber("sizeBytes", e.TotalBytes);
				w.WriteString("url", e.Uri);
				w.WriteString("folder", e.Dir);
				w.WriteString("path", e.Path);
				w.WriteString("category", e.Category);
				w.WriteString("addedUtc", Iso(e.AddedUtc));
				w.WriteString("finishedUtc", e.FinishedUtc is { } f ? Iso(f) : null);
				w.WriteString("error", e.ErrorMessage);
				w.WriteEndObject();
			}
			w.WriteEndArray();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

	/// <summary>RFC 4180 quoting, plus a leading apostrophe on cells a spreadsheet would run as a formula (file names come from the internet).</summary>
	private static string Quote(string? value) {
		value ??= "";
		if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') {
			value = "'" + value;
		}
		return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
	}
}
