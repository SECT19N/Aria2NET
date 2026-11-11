namespace Aria2NET.Core.Settings;

/// <summary>Where downloads go when nothing else was chosen.</summary>
public static class DefaultFolderResolver {
	public static string UserDownloads() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

	/// <summary>The engine's own <c>dir</c>: the default folder setting, else the user's Downloads folder.</summary>
	public static string ForEngine(AppSettings s) =>
		string.IsNullOrWhiteSpace(s.DefaultDownloadFolder) || s.DefaultDownloadFolder.Any(char.IsControl) // the folder goes into aria2's config file
			? UserDownloads()
			: s.DefaultDownloadFolder;

	/// <summary>The folder the Add dialog starts in: last used, then the default, then Downloads.</summary>
	public static string ForAddDialog(AppSettings s) {
		foreach (var candidate in new[] { s.LastUsedFolder, s.DefaultDownloadFolder }) {
			if (!string.IsNullOrWhiteSpace(candidate)) {
				return candidate;
			}
		}
		return UserDownloads();
	}
}
