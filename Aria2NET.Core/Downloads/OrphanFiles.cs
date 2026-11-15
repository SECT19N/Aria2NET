namespace Aria2NET.Core.Downloads;

/// <summary>A <c>.aria2</c> control file left in a download folder that aria2 is not downloading any more (the app crashed, or the download was removed).</summary>
public sealed record OrphanFile(string ControlFile, string DataFile, bool DataExists, long DataBytes);

public static class OrphanFiles {
	/// <summary>Control files in <paramref name="folders"/> that no live download owns. Only the top level of each folder is scanned.</summary>
	public static IReadOnlyList<OrphanFile> Find(IEnumerable<string> folders, IEnumerable<DownloadItem> live) {
		HashSet<string> owned = live.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		List<OrphanFile> result = new List<OrphanFile>();
		foreach (string? folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) {
			if (!Directory.Exists(folder)) {
				continue;
			}
			IEnumerable<string> files;
			try {
				files = Directory.EnumerateFiles(folder, "*.aria2", SearchOption.TopDirectoryOnly).ToList();
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				continue;
			}
			foreach (string control in files) {
				string data = control[..^".aria2".Length];
				if (owned.Contains(data)) {
					continue;
				}
				bool exists = File.Exists(data);
				result.Add(new OrphanFile(control, data, exists, exists ? new FileInfo(data).Length : 0));
			}
		}
		return result;
	}

	/// <summary>Deletes the control files, and the partial data too when <paramref name="deleteData"/> is set.</summary>
	public static int Clean(IEnumerable<OrphanFile> orphans, bool deleteData) {
		int cleaned = 0;
		foreach (OrphanFile orphan in orphans) {
			try {
				File.Delete(orphan.ControlFile);
				if (deleteData && orphan.DataExists) {
					File.Delete(orphan.DataFile);
				}
				cleaned++;
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// In use or protected; leave it for the next scan.
			}
		}
		return cleaned;
	}
}
