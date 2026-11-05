namespace Aria2NET.Core.Data;

/// <summary>Timestamped backup files in one folder, oldest pruned first.</summary>
public static class BackupRotation {
	/// <summary>Path for a new backup of <paramref name="prefix"/>; names sort oldest to newest.</summary>
	public static string NewPath(string dir, string prefix, string extension, DateTime? nowUtc = null) {
		Directory.CreateDirectory(dir);
		var stamp = (nowUtc ?? DateTime.UtcNow).ToString("yyyyMMddHHmmssfff");
		var path = Path.Combine(dir, $"{prefix}-{stamp}{extension}");
		// Two backups inside the same millisecond (tests, or a restart loop) must not overwrite each other.
		for (var n = 1; File.Exists(path); n++) {
			path = Path.Combine(dir, $"{prefix}-{stamp}-{n}{extension}");
		}
		return path;
	}

	/// <summary>Keeps the newest <paramref name="keep"/> backups of <paramref name="prefix"/>.</summary>
	public static void Prune(string dir, string prefix, int keep) {
		if (!Directory.Exists(dir)) {
			return;
		}
		var files = Directory.GetFiles(dir, prefix + "-*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Math.Max(keep, 0));
		foreach (var file in files) {
			try {
				File.Delete(file);
			}
			catch (IOException) {
				// Still in use; the next prune gets it.
			}
		}
	}

	/// <summary>Backups of <paramref name="prefix"/>, newest first.</summary>
	public static IEnumerable<string> Newest(string dir, string prefix) =>
		Directory.Exists(dir)
			? Directory.GetFiles(dir, prefix + "-*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
			: [];

	/// <summary>Copies <paramref name="source"/> into the backup folder and prunes.</summary>
	public static string? CopyAndPrune(string source, string dir, string prefix, int keep, DateTime? nowUtc = null) {
		if (!File.Exists(source)) {
			return null;
		}
		var target = NewPath(dir, prefix, Path.GetExtension(source), nowUtc);
		File.Copy(source, target);
		Prune(dir, prefix, keep);
		return target;
	}
}
