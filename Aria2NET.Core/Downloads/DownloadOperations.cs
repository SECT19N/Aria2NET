using System.Globalization;
using System.Text.Json;
using Aria2NET.Core.Aria2;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;

namespace Aria2NET.Core.Downloads;

public enum QueueMove {
	Top,
	Up,
	Down,
	Bottom,
}

/// <summary>Everything shown in the Properties dialog.</summary>
public sealed record DownloadDetails(
	DownloadItem Item,
	IReadOnlyList<(string Uri, string Status)> Uris,
	int Connections,
	string? InfoHash,
	string? Referer,
	string? Checksum,
	long SpeedLimitBytesPerSec);

/// <summary>Actions on downloads (pause, remove, retry, ...). UI-free so it can be tested against a real aria2.</summary>
public sealed class DownloadOperations(IAria2Engine engine, ILogger<DownloadOperations> logger) {
	private const string MaxConcurrent = "max-concurrent-downloads";

	private readonly Lock _forceLock = new();
	private readonly HashSet<string> _forced = [];
	private Aria2RpcClient? _forcedRpc;
	private int _baseConcurrent;

	private Aria2RpcClient Rpc => engine.Rpc ?? throw new InvalidOperationException("The download engine is not running.");

	public Task PauseAsync(IEnumerable<DownloadItem> items, CancellationToken ct = default) =>
		Check(Rpc.PauseAsync(items.Where(DownloadCapabilities.CanPause).Select(i => i.Gid), ct));

	public Task ResumeAsync(IEnumerable<DownloadItem> items, CancellationToken ct = default) =>
		Check(Rpc.UnpauseAsync(items.Where(DownloadCapabilities.CanResume).Select(i => i.Gid), ct));

	public Task PauseAllAsync(CancellationToken ct = default) => Rpc.PauseAllAsync(ct);

	public Task ResumeAllAsync(CancellationToken ct = default) => Rpc.UnpauseAllAsync(ct);

	/// <summary>
	/// Removes the downloads from aria2 and optionally deletes their files (completed files go to the Recycle Bin).
	/// Returns file problems that did not stop the removal.
	/// </summary>
	public async Task<IReadOnlyList<string>> RemoveAsync(IReadOnlyCollection<DownloadItem> items, bool deleteFiles, CancellationToken ct = default) {
		var rpc = Rpc;
		var live = items.Where(DownloadCapabilities.IsLive).Select(i => i.Gid).ToList();
		if (live.Count > 0) {
			await rpc.RemoveAsync(live, ct);
		}
		// Live downloads become "removed" results, finished ones are results already; forget them all. Failures here mean it is already gone.
		var leftover = await rpc.RemoveResultAsync(items.Select(i => i.Gid), ct);
		if (leftover.Count > 0) {
			logger.LogDebug("removeDownloadResult reported {Count} already-gone entries", leftover.Count);
		}

		var problems = new List<string>();
		if (deleteFiles) {
			foreach (var item in items) {
				await DeleteFilesAsync(item, problems, ct);
			}
		}
		return problems;
	}

	private async Task DeleteFilesAsync(DownloadItem item, List<string> problems, CancellationToken ct) {
		if (string.IsNullOrEmpty(item.Path)) {
			return;
		}
		var toRecycle = item.Status == DownloadStatus.Complete;
		foreach (var path in new[] { item.Path, item.Path + ".aria2" }) {
			// aria2 may still be closing the file; give it a moment.
			for (var attempt = 0; attempt < 5; attempt++) {
				try {
					if (File.Exists(path)) {
						if (toRecycle && path == item.Path) {
							FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
						}
						else {
							File.Delete(path);
						}
					}
					break;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
					if (attempt == 4) {
						logger.LogWarning(ex, "Could not delete {Path}", path);
						problems.Add($"{path}: {ex.Message}");
					}
					else {
						await Task.Delay(200, ct);
					}
				}
			}
		}
	}

	/// <summary>Starts the download again from its original address. Returns the new GID.</summary>
	public async Task<string> RetryAsync(DownloadItem item, CancellationToken ct = default) {
		if (!DownloadCapabilities.CanRetry(item)) {
			throw new InvalidOperationException("This download cannot be retried.");
		}
		var rpc = Rpc;
		var options = new Dictionary<string, string>();
		if (!string.IsNullOrEmpty(item.Dir)) {
			options["dir"] = item.Dir;
		}
		if (!string.IsNullOrEmpty(item.Path)) {
			options["out"] = item.Name;
		}
		var gid = await rpc.AddUriAsync([item.FirstUri!], options, ct);
		await rpc.RemoveResultAsync([item.Gid], ct);
		return gid;
	}

	/// <summary>Forgets finished entries (complete, failed). Files stay.</summary>
	public Task ClearAsync(IEnumerable<DownloadItem> items, CancellationToken ct = default) =>
		Check(Rpc.RemoveResultAsync(items.Where(i => !DownloadCapabilities.IsLive(i)).Select(i => i.Gid), ct));

	public async Task MoveAsync(DownloadItem item, QueueMove move, CancellationToken ct = default) {
		var (pos, how) = move switch {
			QueueMove.Top => (0, "POS_SET"),
			QueueMove.Up => (-1, "POS_CUR"),
			QueueMove.Down => (1, "POS_CUR"),
			_ => (0, "POS_END"),
		};
		await Rpc.ChangePositionAsync(item.Gid, pos, how, ct);
	}

	/// <summary>
	/// aria2 has no "start now": move it to the front and allow one more simultaneous download until it finishes.
	/// Call <see cref="OnSnapshot"/> with each poll so the limit is put back.
	/// </summary>
	public async Task ForceStartAsync(DownloadItem item, CancellationToken ct = default) {
		var rpc = Rpc;
		await rpc.ChangePositionAsync(item.Gid, 0, "POS_SET", ct);
		int target;
		lock (_forceLock) {
			if (!ReferenceEquals(_forcedRpc, rpc)) {
				// New engine instance: its options were reset, so the old bookkeeping is meaningless.
				_forced.Clear();
				_forcedRpc = rpc;
				_baseConcurrent = 0;
			}
		}
		if (_baseConcurrent == 0) {
			var global = await rpc.GetGlobalOptionAsync(ct);
			_baseConcurrent = global.TryGetValue(MaxConcurrent, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 5;
		}
		lock (_forceLock) {
			_forced.Add(item.Gid);
			target = _baseConcurrent + _forced.Count;
		}
		await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { [MaxConcurrent] = target.ToString(CultureInfo.InvariantCulture) }, ct);
	}

	/// <summary>Puts the simultaneous-download limit back once force-started downloads are no longer running.</summary>
	public async Task OnSnapshot(PollSnapshot snapshot, CancellationToken ct = default) {
		Aria2RpcClient? rpc;
		int target;
		lock (_forceLock) {
			if (_forced.Count == 0 || _forcedRpc is null) {
				return;
			}
			rpc = _forcedRpc;
			// Still waiting for a slot or running: keep the extra slot.
			var keep = _forced.Where(gid => snapshot.Items.Any(i => i.Gid == gid && i.Status is DownloadStatus.Active or DownloadStatus.Waiting)).ToList();
			if (keep.Count == _forced.Count) {
				return;
			}
			_forced.Clear();
			foreach (var gid in keep) {
				_forced.Add(gid);
			}
			target = _baseConcurrent + _forced.Count;
		}
		try {
			await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { [MaxConcurrent] = target.ToString(CultureInfo.InvariantCulture) }, ct);
		}
		catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException) {
			logger.LogWarning(ex, "Could not restore {Option}", MaxConcurrent);
		}
	}

	public async Task RenameAsync(DownloadItem item, string newName, CancellationToken ct = default) {
		newName = newName.Trim();
		if (newName.Length == 0 || newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) {
			throw new ArgumentException("That is not a valid file name.", nameof(newName));
		}
		await Rpc.ChangeOptionAsync(item.Gid, new Dictionary<string, string> { ["out"] = newName }, ct);
	}

	public async Task SetFolderAsync(DownloadItem item, string folder, CancellationToken ct = default) =>
		await Rpc.ChangeOptionAsync(item.Gid, new Dictionary<string, string> { ["dir"] = folder }, ct);

	/// <summary>0 means unlimited.</summary>
	public async Task SetSpeedLimitAsync(DownloadItem item, long bytesPerSecond, CancellationToken ct = default) =>
		await Rpc.ChangeOptionAsync(item.Gid, new Dictionary<string, string> { ["max-download-limit"] = bytesPerSecond.ToString(CultureInfo.InvariantCulture) }, ct);

	public async Task<DownloadDetails> GetDetailsAsync(string gid, CancellationToken ct = default) {
		var rpc = Rpc;
		using var status = await rpc.TellStatusFullAsync(gid, ct);
		var root = status.RootElement;
		var item = DownloadItem.FromJson(root);
		var uris = new List<(string, string)>();
		if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0
			&& files[0].TryGetProperty("uris", out var list) && list.ValueKind == JsonValueKind.Array) {
			foreach (var u in list.EnumerateArray()) {
				uris.Add((Str(u, "uri") ?? "", Str(u, "status") ?? ""));
			}
		}

		var options = await rpc.GetOptionAsync(gid, ct);
		return new DownloadDetails(
			item, uris,
			int.TryParse(Str(root, "connections"), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0,
			Str(root, "infoHash"),
			options.GetValueOrDefault("referer"),
			options.GetValueOrDefault("checksum"),
			options.TryGetValue("max-download-limit", out var limit) && long.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : 0);
	}

	private static string? Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static async Task Check(Task<IReadOnlyList<string>> batch) {
		var failures = await batch;
		if (failures.Count > 0) {
			throw new Aria2RpcException(0, string.Join("; ", failures.Distinct()));
		}
	}
}
