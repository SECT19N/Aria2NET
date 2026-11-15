using Aria2NET.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Downloads;

/// <summary>
/// Decides when a download that failed because its disk was not ready (a drive that powers down when idle, a network share that
/// went to sleep) should be tried again. Pure bookkeeping: the caller supplies the time and does the retrying.
/// </summary>
public sealed class DiskRetryPlanner {
	/// <summary>How long to wait before each further attempt. After the last one the failure is left for the user.</summary>
	public static IReadOnlyList<TimeSpan> Delays { get; } = [
		TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
		TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
	];

	private static readonly TimeSpan Forget = TimeSpan.FromMinutes(30);

	private readonly Lock _gate = new();
	private readonly Dictionary<string, Tries> _tries = [];

	private sealed class Tries(int made, DateTimeOffset since) {
		public int Made { get; set; } = made;
		public DateTimeOffset Since { get; } = since;
		public DateTimeOffset? Due { get; set; }
		public bool Running { get; set; }
	}

	/// <summary>
	/// aria2 reports "could not open the existing file" (15), "could not create the file" (16), "disk read/write error" (17) and
	/// "could not create the folder" (18) when the disk is not there or not awake. Only a download that can be started again qualifies.
	/// </summary>
	public static bool IsDiskError(DownloadItem item) =>
		item.Status == DownloadStatus.Error && item.ErrorCode is 15 or 16 or 17 or 18 && DownloadCapabilities.CanRetry(item);

	/// <summary>True while another attempt is still going to be made, so the failure is not worth announcing yet.</summary>
	public bool WillRetry(DownloadItem item) {
		if (!IsDiskError(item)) {
			return false;
		}
		lock (_gate) {
			return !_tries.TryGetValue(item.Gid, out Tries? t) || t.Made < Delays.Count;
		}
	}

	/// <summary>Looks at a poll and returns the failed downloads whose waiting time is over. Each is returned once until <see cref="Started"/> or <see cref="Failed"/>.</summary>
	public IReadOnlyList<DownloadItem> Due(PollSnapshot snapshot, DateTimeOffset now) {
		List<DownloadItem> due = [];
		lock (_gate) {
			foreach (DownloadItem? item in snapshot.Items.Where(IsDiskError)) {
				if (!_tries.TryGetValue(item.Gid, out Tries? t)) {
					t = _tries[item.Gid] = new Tries(0, now);
				}
				if (t.Running || t.Made >= Delays.Count) {
					continue;
				}
				t.Due ??= now + Delays[t.Made];
				if (t.Due <= now) {
					t.Running = true;
					due.Add(item);
				}
			}
			HashSet<string> present = snapshot.Items.Select(i => i.Gid).ToHashSet(StringComparer.Ordinal);
			foreach (string? gone in _tries.Where(p => now - p.Value.Since > Forget && !p.Value.Running && !present.Contains(p.Key)).Select(p => p.Key).ToList()) {
				_tries.Remove(gone); // finished or removed long ago
			}
		}
		return due;
	}

	/// <summary>The attempt for <paramref name="oldGid"/> started a new download; remember how many tries it took.</summary>
	public void Started(string oldGid, string newGid, DateTimeOffset now) {
		lock (_gate) {
			int made = _tries.TryGetValue(oldGid, out Tries? t) ? t.Made + 1 : 1;
			_tries.Remove(oldGid);
			_tries[newGid] = new Tries(made, now);
		}
	}

	/// <summary>The attempt could not even be made (disk still asleep, engine busy): count it and wait for the next delay.</summary>
	public void Failed(string gid) {
		lock (_gate) {
			if (_tries.TryGetValue(gid, out Tries? t)) {
				t.Made++;
				t.Due = null;
				t.Running = false;
			}
		}
	}

	/// <summary>The attempt was dropped without counting (the setting was switched off, or the download is gone).</summary>
	public void Release(string gid) {
		lock (_gate) {
			if (_tries.TryGetValue(gid, out Tries? t)) {
				t.Running = false;
			}
		}
	}
}

/// <summary>Touches a folder so a sleeping disk spins up, and tells whether it can be written to.</summary>
public static class DiskWake {
	/// <summary>Creates the folder if needed and writes and deletes a tiny file in it. Reading or writing a sleeping disk blocks until it is awake.</summary>
	public static bool TryTouch(string dir) {
		try {
			Directory.CreateDirectory(dir);
			string probe = Path.Combine(dir, ".aria2net-" + Guid.NewGuid().ToString("N"));
			using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) {
			}
			return true;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) {
			return false;
		}
	}

	/// <summary>Keeps touching <paramref name="dir"/> until it is writable or <paramref name="timeout"/> is over. An empty folder name counts as ready.</summary>
	public static async Task<bool> WaitAsync(string? dir, TimeSpan timeout, CancellationToken ct, Func<string, bool>? touch = null, TimeSpan? pause = null) {
		if (string.IsNullOrWhiteSpace(dir)) {
			return true;
		}
		touch ??= TryTouch;
		TimeSpan wait = pause ?? TimeSpan.FromSeconds(3);
		DateTime deadline = DateTime.UtcNow + timeout;
		while (true) {
			if (await Task.Run(() => touch(dir), ct)) {
				return true;
			}
			if (DateTime.UtcNow + wait > deadline) {
				return false;
			}
			await Task.Delay(wait, ct);
		}
	}
}

/// <summary>Starts a download again after a disk problem, once the disk answers. Runs off the monitor's polls.</summary>
public sealed class DiskRetryService(DownloadMonitor monitor, DownloadOperations operations, SettingsStore<AppSettings> settings, ILogger<DiskRetryService> logger, TimeProvider? clock = null) {
	private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(60);

	private readonly TimeProvider _clock = clock ?? TimeProvider.System;
	private readonly DiskRetryPlanner _planner = new();
	private bool _started;

	/// <summary>True while another try is coming; the notification of the failure waits for the last one.</summary>
	public bool WillRetry(DownloadItem item) => settings.Load().RetryWhenDiskNotReady && _planner.WillRetry(item);

	public void Start() {
		if (_started) {
			return;
		}
		_started = true;
		monitor.SnapshotReady += (_, snapshot) => OnSnapshot(snapshot);
	}

	private void OnSnapshot(PollSnapshot snapshot) {
		if (!settings.Load().RetryWhenDiskNotReady) {
			return;
		}
		foreach (DownloadItem item in _planner.Due(snapshot, _clock.GetUtcNow())) {
			_ = RetryAsync(item);
		}
	}

	private async Task RetryAsync(DownloadItem item) {
		try {
			if (!await DiskWake.WaitAsync(item.Dir, WakeTimeout, CancellationToken.None)) {
				logger.LogInformation("The folder {Dir} is still not writable; will try {Name} again later", item.Dir, item.Name);
				_planner.Failed(item.Gid);
				return;
			}
			string gid = await operations.RetryAsync(item);
			_planner.Started(item.Gid, gid, _clock.GetUtcNow());
			logger.LogInformation("{Name} failed because its disk was not ready; started it again", item.Name);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or Aria2.Aria2RpcException or IOException) {
			logger.LogWarning(ex, "Starting {Name} again failed", item.Name);
			_planner.Failed(item.Gid);
		}
	}
}
