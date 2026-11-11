namespace Aria2NET.Core.Shell;

/// <summary>
/// Decides what a second launch does when another copy of Aria2NET is already running. A newer copy must not hand over to an older one
/// (the user would keep seeing the old version after installing the new one), so it asks the old copy to exit instead.
/// </summary>
public static class InstanceRules {
	/// <summary>
	/// Name of the event a newer copy sets to ask the running copy to exit (it stops aria2 and saves its session first). <c>Local\</c> keeps
	/// it per Windows session. Copies from before this existed never create it, which is how the newer copy knows it has to close them itself.
	/// </summary>
	public const string ReplaceEventName = @"Local\Aria2NET.ReplaceRequest";

	/// <summary>
	/// Reads "0.8.0", "0.8.0.0", "0.8.0+abc123" or "0.8.0-rc.1" as 0.8.0 (a pre-release suffix is ignored, so 0.8.0-rc.1 counts as 0.8.0).
	/// Returns null for anything else.
	/// </summary>
	public static Version? ParseVersion(string? text) {
		if (string.IsNullOrWhiteSpace(text)) {
			return null;
		}
		var core = text.Trim().Split('+', '-')[0];
		if (!Version.TryParse(core, out var version) || version.Major < 0 || version.Minor < 0) {
			return null;
		}
		return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
	}

	/// <summary>
	/// True when the running copy is older than this one, so this one should take over. An unreadable version of the running copy counts as
	/// older: copies from before version stamping, or a damaged exe, must not be allowed to keep the newer one out.
	/// </summary>
	public static bool RunningCopyIsOlder(string? runningVersion, Version mine) =>
		ParseVersion(runningVersion) is not { } running || running < mine;
}
