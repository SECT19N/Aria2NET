using System.Text.Json.Serialization;
using Aria2NET.Core.Settings;

namespace Aria2NET.Core.Downloads;

/// <summary>A saved set of headers for one site: "download from this site with these headers". The cookie is stored protected for this Windows account.</summary>
public sealed class HeaderPreset {
	public string Name { get; set; } = "";
	public List<string> Headers { get; set; } = [];
	public string? Referer { get; set; }
	/// <summary>The cookie, protected with <see cref="SecretProtector"/>.</summary>
	public string? CookieProtected { get; set; }
}

public sealed class HeaderPresetFile {
	public int SchemaVersion { get; set; } = 1;
	public List<HeaderPreset> Presets { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HeaderPresetFile))]
public sealed partial class HeaderPresetJsonContext : JsonSerializerContext;

/// <summary>Reads and writes the saved header presets (<c>presets.json</c>).</summary>
public sealed class HeaderPresets(SettingsStore<HeaderPresetFile> store) {
	public const int MaxPresets = 50;
	public const int MaxNameLength = 60;

	public IReadOnlyList<HeaderPreset> List() =>
		store.Load().Presets.Where(p => p.Name.Length > 0).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

	/// <summary>Saves under <paramref name="name"/>, replacing a preset with the same name. Returns an error text, or null when saved.</summary>
	public string? Save(string name, IEnumerable<string> headers, string? referer, string? cookie) {
		name = name.Trim();
		if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl)) {
			return $"Give the preset a name of up to {MaxNameLength} characters.";
		}
		var lines = headers.Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
		var check = new AddOptions(lines, referer, cookie).Validate();
		if (check is not null) {
			return check;
		}
		if (lines.Count == 0 && string.IsNullOrWhiteSpace(referer) && string.IsNullOrWhiteSpace(cookie)) {
			return "There is nothing to save: add a header, a referer or a cookie first.";
		}
		try {
			var file = store.Load();
			file.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
			if (file.Presets.Count >= MaxPresets) {
				return $"You can keep up to {MaxPresets} presets; delete one first.";
			}
			file.Presets.Add(new HeaderPreset {
				Name = name,
				Headers = lines,
				Referer = string.IsNullOrWhiteSpace(referer) ? null : referer.Trim(),
				CookieProtected = string.IsNullOrWhiteSpace(cookie) ? null : SecretProtector.Protect(cookie.Trim()),
			});
			store.Save(file);
			return null;
		}
		catch (IOException ex) {
			return "Could not save the preset: " + ex.Message;
		}
	}

	public void Delete(string name) {
		try {
			var file = store.Load();
			if (file.Presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)) > 0) {
				store.Save(file);
			}
		}
		catch (IOException) {
			// Leaving the preset in place is harmless.
		}
	}

	public static string Cookie(HeaderPreset preset) => SecretProtector.Unprotect(preset.CookieProtected);
}
