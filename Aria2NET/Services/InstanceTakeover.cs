using System.Diagnostics;
using Aria2NET.Core.Shell;
using Microsoft.UI.Dispatching;

namespace Aria2NET.Services;

/// <summary>
/// What a newer copy does when an older one is already running. Without this the newer copy hands its launch to the older one and exits,
/// so after installing an update the user keeps seeing the old version.
/// </summary>
internal static class InstanceTakeover {
	private const string ProcessName = "Aria2NET";

	/// <summary>
	/// Closes older copies of Aria2NET that run in this Windows session. Returns true when none is left, so this launch can become the main
	/// instance. Returns false when there was nothing older to replace (the launch should be handed over as usual) or the user declined.
	/// </summary>
	public static bool ReplaceOlderCopies(Version mine) {
		var older = OlderCopies(mine);
		if (older.Count == 0) {
			return false;
		}

		// A copy that has this code listens for the event and exits cleanly. Older ones never created it.
		if (EventWaitHandle.TryOpenExisting(InstanceRules.ReplaceEventName, out var request)) {
			using (request) {
				request.Set();
			}
			WaitForExit(older, TimeSpan.FromSeconds(20));
			older = older.Where(IsRunning).ToList();
		}

		if (older.Count > 0) {
			var versions = string.Join(", ", older.Select(VersionOf).Distinct());
			var answer = NativeMethods.MessageBox(IntPtr.Zero,
				$"An older version of Aria2NET ({versions}) is still running and would take over this one.\n\nClose it and start version {mine.ToString(3)}? Downloads continue where they stopped.",
				"Aria2NET", 0x4 /* MB_YESNO */ | 0x20 /* MB_ICONQUESTION */);
			if (answer != 6 /* IDYES */) {
				return false;
			}
			foreach (var process in older) {
				try {
					process.Kill(entireProcessTree: true); // takes the old copy's aria2c with it
				} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
					// already gone, or not ours to kill; the wait below decides
				}
			}
			WaitForExit(older, TimeSpan.FromSeconds(10));
		}

		var left = older.Where(IsRunning).ToList();
		foreach (var process in older) {
			process.Dispose();
		}
		return left.Count == 0;
	}

	/// <summary>Other Aria2NET processes of this Windows session whose version is lower than <paramref name="mine"/>.</summary>
	private static List<Process> OlderCopies(Version mine) {
		var self = Environment.ProcessId;
		var session = Process.GetCurrentProcess().SessionId;
		var result = new List<Process>();
		foreach (var process in Process.GetProcessesByName(ProcessName)) {
			if (process.Id != self && process.SessionId == session && InstanceRules.RunningCopyIsOlder(VersionOf(process), mine)) {
				result.Add(process);
			} else {
				process.Dispose();
			}
		}
		return result;
	}

	private static string? VersionOf(Process process) {
		try {
			return process.MainModule?.FileVersionInfo.FileVersion;
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			return null; // access denied or exited meanwhile: counts as older, see InstanceRules
		}
	}

	private static bool IsRunning(Process process) {
		try {
			return !process.HasExited;
		} catch (InvalidOperationException) {
			return false;
		}
	}

	private static void WaitForExit(List<Process> processes, TimeSpan timeout) {
		var deadline = DateTime.UtcNow + timeout;
		foreach (var process in processes) {
			try {
				var left = deadline - DateTime.UtcNow;
				if (left > TimeSpan.Zero) {
					process.WaitForExit(left);
				}
			} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
				// not running any more
			}
		}
	}

	/// <summary>
	/// The running copy's side: waits for a newer copy to ask it to exit, then runs <paramref name="exit"/> on the UI thread. The thread
	/// is a background thread, so it never keeps the process alive.
	/// </summary>
	public static void ListenForReplaceRequest(DispatcherQueue dispatcher, Action exit) {
		var request = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceRules.ReplaceEventName);
		new Thread(() => {
			while (request.WaitOne()) {
				dispatcher.TryEnqueue(() => exit());
			}
		}) { IsBackground = true, Name = "ReplaceRequest" }.Start();
	}
}
