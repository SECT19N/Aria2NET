using System.Globalization;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Torrents;

public enum SeedMode {
	/// <summary>Whatever aria2 is configured with (the seeding rule in Settings; ratio 1.0 unless changed).</summary>
	Default,
	Forever,
	/// <summary>Stop once uploaded/downloaded reaches <see cref="SeedingOverride.Ratio"/>.</summary>
	Ratio,
	/// <summary>Stop as soon as the data is complete.</summary>
	Never,
}

public sealed record SeedingOverride(SeedMode Mode = SeedMode.Default, double Ratio = 1.0) {
	public static SeedingOverride Default { get; } = new();

	/// <summary>aria2 options for this choice. Note aria2 reads <c>seed-ratio=0.0</c> as "no ratio limit" and <c>seed-time=0</c> as "do not seed".</summary>
	/// <remarks>aria2 stops at whichever of ratio and time comes first, so these also lift a time limit set in Settings.</remarks>
	public IReadOnlyDictionary<string, string> ToOptions() => Mode switch {
		SeedMode.Forever => new Dictionary<string, string> { ["seed-ratio"] = "0.0", ["seed-time"] = Settings.EngineOptions.NoSeedTime },
		SeedMode.Ratio => new Dictionary<string, string> {
			["seed-ratio"] = Math.Max(0.1, Ratio).ToString("0.0#", CultureInfo.InvariantCulture),
			["seed-time"] = Settings.EngineOptions.NoSeedTime,
		},
		SeedMode.Never => new Dictionary<string, string> { ["seed-time"] = "0" },
		_ => [],
	};
}

public enum AllocationChoice {
	/// <summary>Whatever the engine is configured with: files grow as data arrives (<c>none</c>).</summary>
	Default,
	/// <summary>Files grow as data arrives (<c>none</c>); no space is reserved up front.</summary>
	Sparse,
	/// <summary>The whole size is reserved at once (<c>falloc</c>), so a full disk is noticed immediately.</summary>
	Full,
}

/// <summary>What the user decided about a torrent before it starts.</summary>
public sealed record TorrentAddRequest(
	string Dir,
	IReadOnlyList<int>? SelectedFiles = null,
	int TotalFiles = 0,
	SeedingOverride? Seeding = null,
	AllocationChoice Allocation = AllocationChoice.Default,
	AddOptions? Extra = null) {

	/// <param name="includeDir">False when changing a download that already exists: aria2 ignores a new <c>dir</c> for it (checked against 1.37.0).</param>
	public IReadOnlyDictionary<string, string> ToOptions(bool includeDir = true) {
		Dictionary<string, string> options = [];
		if (includeDir) {
			options["dir"] = Dir;
		}
		if (SelectedFiles is { Count: > 0 } && SelectFileSpec.Build(SelectedFiles, TotalFiles) is { } spec) {
			options["select-file"] = spec;
		}
		foreach ((string? k, string? v) in (Extra ?? AddOptions.None).ToOptions()) {
			options[k] = v;
		}
		foreach ((string? k, string? v) in (Seeding ?? SeedingOverride.Default).ToOptions()) {
			options[k] = v;
		}
		switch (Allocation) {
			case AllocationChoice.Sparse:
				options["file-allocation"] = "none";
				break;
			case AllocationChoice.Full:
				options["file-allocation"] = "falloc";
				break;
		}
		return options;
	}
}

/// <summary>A magnet whose metadata arrived: the paused download that will fetch the content, and the files it has.</summary>
public sealed record ResolvedMagnet(string Gid, string Name, IReadOnlyList<DownloadFile> Files);

/// <summary>Where a torrent that is being added again already is.</summary>
public sealed record DuplicateTorrent(string? LiveGid, HistoryEntry? Past);

/// <summary>Adding torrents, magnets and metalinks. UI-free so the whole flow is testable against a real aria2.</summary>
public sealed class TorrentOperations(IAria2Engine engine, ILogger<TorrentOperations>? logger = null) {
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

	private Aria2RpcClient Rpc => engine.Rpc ?? throw new InvalidOperationException("The download engine is not running.");

	/// <summary>Adds a .torrent file. With <paramref name="paused"/> nothing is downloaded until <see cref="StartAsync"/>.</summary>
	public async Task<string> AddTorrentAsync(byte[] torrent, TorrentAddRequest request, bool paused = false, CancellationToken ct = default) {
		EnsureFolder(request.Dir);
		Dictionary<string, string> options = request.ToOptions().ToDictionary(p => p.Key, p => p.Value);
		if (paused) {
			options["pause"] = "true";
		}
		return await Rpc.AddTorrentAsync(torrent, options, ct: ct);
	}

	/// <summary>
	/// Adds a magnet link. aria2 first fetches the torrent's metadata (this GID), then creates a paused download for the content
	/// (see <see cref="WaitForMetadataAsync"/>), so the user can pick files before anything is downloaded.
	/// </summary>
	public async Task<string> AddMagnetAsync(string magnet, string dir, CancellationToken ct = default) {
		// aria2 saves the fetched .torrent into this folder, but only if it already exists.
		EnsureFolder(dir);
		return await Rpc.AddUriAsync([magnet], new Dictionary<string, string> { ["dir"] = dir, ["pause-metadata"] = "true" }, ct);
	}

	/// <summary>
	/// Waits for a magnet's metadata. Returns the paused follower and its files, or null when the fetch failed or the user removed it.
	/// Cancelling <paramref name="ct"/> stops waiting without touching aria2.
	/// </summary>
	public async Task<ResolvedMagnet?> WaitForMetadataAsync(string metadataGid, CancellationToken ct = default) {
		while (true) {
			DownloadItem item;
			try {
				item = await Rpc.TellStatusAsync(metadataGid, ct);
			} catch (Aria2RpcException) {
				return null; // removed and forgotten
			}
			if (item.Status is DownloadStatus.Error or DownloadStatus.Removed) {
				return null;
			}
			if (item.Status == DownloadStatus.Complete) {
				if (item.FollowedBy is not { } follower) {
					return null;
				}
				DownloadItem followerItem = await Rpc.TellStatusAsync(follower, ct);
				return new ResolvedMagnet(follower, followerItem.Name, await Rpc.GetFilesAsync(follower, ct));
			}
			await Task.Delay(PollInterval, ct);
		}
	}

	/// <summary>Applies the user's choices (not the folder, which is fixed when the download is added) to a paused download and starts it.</summary>
	public async Task StartAsync(string gid, TorrentAddRequest request, CancellationToken ct = default) {
		await Rpc.ChangeOptionAsync(gid, request.ToOptions(includeDir: false), ct);
		IReadOnlyList<string> failures = await Rpc.UnpauseAsync([gid], ct);
		if (failures.Count > 0) {
			throw new InvalidOperationException(failures[0]);
		}
	}

	/// <summary>Forgets a download that was never started (the user closed the file picker).</summary>
	public async Task DiscardAsync(string gid, CancellationToken ct = default) {
		await Rpc.RemoveAsync([gid], ct);
		await Rpc.RemoveResultAsync([gid], ct);
	}

	/// <summary>Adds a metalink file. aria2 starts one download per file in it.</summary>
	public async Task<IReadOnlyList<string>> AddMetalinkAsync(byte[] metalink, string dir, AddOptions? extra = null, CancellationToken ct = default) {
		EnsureFolder(dir);
		Dictionary<string, string> options = (extra ?? AddOptions.None).ToOptions().ToDictionary(p => p.Key, p => p.Value);
		options["dir"] = dir;
		return await Rpc.AddMetalinkAsync(metalink, options, ct);
	}

	/// <summary>Finds the torrent among the running downloads, or in History if it was added before.</summary>
	public static DuplicateTorrent? FindDuplicate(string infoHash, IEnumerable<DownloadItem> live, HistoryStore? history) {
		DownloadItem? running = live.FirstOrDefault(i => !i.IsMetadata && string.Equals(i.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
		if (running is not null) {
			return new DuplicateTorrent(running.Gid, null);
		}
		HistoryEntry? past = history?.FindByInfoHash(infoHash);
		return past is null ? null : new DuplicateTorrent(null, past);
	}

	private void EnsureFolder(string dir) {
		try {
			Directory.CreateDirectory(dir);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
			logger?.LogWarning(ex, "Could not create {Dir}", dir);
			throw new IOException($"Cannot use the folder {dir}: {ex.Message}", ex);
		}
	}

	/// <summary>Changes when a running or finished torrent stops seeding.</summary>
	public Task ApplySeedingAsync(string gid, SeedingOverride seeding, CancellationToken ct = default) =>
		Rpc.ChangeOptionAsync(gid, seeding.ToOptions(), ct);

	/// <summary>
	/// Verifies the files on disk against the torrent and downloads whatever is damaged or missing: pauses the download, asks for
	/// <c>check-integrity</c>, and starts it again (aria2 checks when a download starts).
	/// </summary>
	public async Task RecheckAsync(string gid, CancellationToken ct = default) {
		Aria2RpcClient rpc = Rpc;
		await PauseAndWaitAsync(rpc, gid, ct);
		await rpc.ChangeOptionAsync(gid, new Dictionary<string, string> { ["check-integrity"] = "true" }, ct);
		IReadOnlyList<string> failures = await rpc.UnpauseAsync([gid], ct);
		if (failures.Count > 0) {
			throw new InvalidOperationException(failures[0]);
		}
	}

	/// <summary>Pauses a download and waits until aria2 says it is paused. False when it did not get there in time.</summary>
	private static async Task<bool> PauseAndWaitAsync(Aria2RpcClient rpc, string gid, CancellationToken ct) {
		await rpc.PauseAsync([gid], ct);
		DateTime deadline = DateTime.UtcNow.AddSeconds(10);
		while ((await rpc.TellStatusAsync(gid, ct)).Status != DownloadStatus.Paused) {
			if (DateTime.UtcNow >= deadline) {
				return false;
			}
			await Task.Delay(100, ct);
		}
		return true;
	}

	/// <summary>
	/// Chooses which files of a torrent are downloaded. aria2 only reads <c>select-file</c> when a download starts, so a running one is
	/// paused, changed and started again; what is already on disk is kept. Skipped files are not deleted.
	/// </summary>
	/// <param name="indexes">1-based indexes of the files to download; at least one.</param>
	public async Task SelectFilesAsync(string gid, IReadOnlyCollection<int> indexes, int totalFiles, CancellationToken ct = default) {
		string? spec = SelectFileSpec.Build(indexes, totalFiles);
		Aria2RpcClient rpc = Rpc;
		DownloadStatus status = (await rpc.TellStatusAsync(gid, ct)).Status;
		if (status is not (DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused)) {
			throw new InvalidOperationException("The files of a finished download cannot be changed.");
		}
		bool wasPaused = status == DownloadStatus.Paused;
		if (!wasPaused && !await PauseAndWaitAsync(rpc, gid, ct)) {
			throw new InvalidOperationException("The download did not pause in time. Try again in a moment.");
		}
		try {
			await rpc.ChangeOptionAsync(gid, new Dictionary<string, string> { ["select-file"] = spec ?? $"1-{totalFiles}" }, ct);
		} finally {
			if (!wasPaused) {
				IReadOnlyList<string> failures = await rpc.UnpauseAsync([gid], CancellationToken.None);
				if (failures.Count > 0) {
					logger?.LogWarning("Could not resume {Gid} after changing its files: {Failure}", gid, failures[0]);
				}
			}
		}
	}
}
