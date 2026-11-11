using System.Runtime.InteropServices;

namespace Aria2NET.Core.Downloads;

/// <summary>Free space of the volume a folder lives on (local drives and network shares), for "will it fit?" checks.</summary>
public static class DiskSpace {
	[DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

	/// <summary>
	/// Bytes available to the current user where <paramref name="folder"/> is or would be created, or null when it cannot be told
	/// (empty or invalid path, unreachable share). A folder that does not exist yet is measured through its nearest existing parent.
	/// </summary>
	public static long? FreeBytes(string? folder) {
		if (string.IsNullOrWhiteSpace(folder)) {
			return null;
		}
		string? probe;
		try {
			probe = Path.GetFullPath(folder);
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return null;
		}
		while (probe is not null && !Directory.Exists(probe)) {
			probe = Path.GetDirectoryName(probe);
		}
		if (probe is null || !GetDiskFreeSpaceEx(probe, out var free, out _, out _)) {
			return null;
		}
		return free > long.MaxValue ? long.MaxValue : (long) free;
	}

	/// <summary>False only when the free space is known and smaller than <paramref name="requiredBytes"/>; an unknown amount never blocks a download.</summary>
	public static bool Fits(long requiredBytes, string? folder) =>
		requiredBytes <= 0 || FreeBytes(folder) is not { } free || free >= requiredBytes;
}
