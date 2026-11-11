using Aria2NET.Core.Settings;

namespace Aria2NET.Core.Downloads;

/// <summary>What to do with one download whose file already exists.</summary>
public enum ConflictChoice {
	/// <summary>Keep both: the new file is saved as <c>name.1.ext</c>.</summary>
	Rename,
	/// <summary>Replace the existing file.</summary>
	Overwrite,
	/// <summary>Do not add this download.</summary>
	Skip,
}

/// <summary>Finds out whether a link would overwrite a file, and the aria2 options for each way to deal with it.</summary>
public static class FileConflicts {
	/// <summary>
	/// The existing file this link would be saved over, or null. Only a best guess: the name comes from the file name typed in the dialog or from
	/// the end of the link, because a name given by the server (Content-Disposition) is not known before the download starts. A file with an
	/// <c>.aria2</c> control file next to it is an unfinished download of aria2's own, which is resumed, not a clash.
	/// </summary>
	public static string? FindExisting(string folder, string url, string? fileName) {
		try {
			var name = fileName?.Trim();
			if (string.IsNullOrEmpty(name)) {
				if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp")) {
					return null;
				}
				name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
			}
			if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
				return null;
			}
			var path = Path.Combine(folder, name);
			return File.Exists(path) && !File.Exists(path + ".aria2") ? path : null;
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UriFormatException) {
			return null;
		}
	}

	/// <summary>
	/// Per-download options for a choice. <c>continue=false</c> matters: with the engine's <c>continue=true</c>, aria2 would treat an existing
	/// file as a partly downloaded one and resume it (or call it done) instead of renaming or replacing it.
	/// </summary>
	public static IReadOnlyDictionary<string, string> OptionsFor(ConflictChoice choice) => choice switch {
		ConflictChoice.Overwrite => new Dictionary<string, string> { ["allow-overwrite"] = "true", ["auto-file-renaming"] = "false", ["continue"] = "false" },
		ConflictChoice.Rename => new Dictionary<string, string> { ["allow-overwrite"] = "false", ["auto-file-renaming"] = "true", ["continue"] = "false" },
		_ => new Dictionary<string, string>(),
	};

	/// <summary>The choice that follows from the policy without asking; null for <see cref="ConflictPolicy.Ask"/>.</summary>
	public static ConflictChoice? FromPolicy(ConflictPolicy policy) => policy switch {
		ConflictPolicy.Overwrite => ConflictChoice.Overwrite,
		ConflictPolicy.AutoRename => ConflictChoice.Rename,
		_ => null,
	};
}
