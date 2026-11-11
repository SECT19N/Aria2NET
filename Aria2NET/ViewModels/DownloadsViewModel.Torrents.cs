using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using Aria2NET.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.ViewModels;

/// <summary>What the file picker shows for a torrent that is about to start.</summary>
/// <param name="FolderEditable">False for a magnet: aria2 fixes the folder when the magnet is added.</param>
public sealed record TorrentPrompt(string Name, IReadOnlyList<TorrentFileEntry> Files, string Folder, bool FolderEditable, string? Note);

/// <summary>What the user chose in the file picker.</summary>
public sealed record TorrentChoice(string Folder, IReadOnlyList<int> Files, SeedingOverride Seeding, AllocationChoice Allocation);

// Adding torrents, magnets and metalinks. The decisions live in Core (TorrentOperations); this part only talks to the user.
public sealed partial class DownloadsViewModel {
	private readonly TorrentOperations _torrents;
	private readonly NoticeCenter _notices;

	/// <summary>Set by the page: shows the file picker and returns the choice (no files chosen means the user cancelled). Without it, everything is downloaded.</summary>
	public Func<TorrentPrompt, Task<TorrentChoice>>? PromptTorrent { get; set; }

	private async Task<bool> AddTorrentFileAsync(string path, string folder, AddOptions? extra = null) {
		var info = new FileInfo(path);
		if (!info.Exists) {
			throw new FileNotFoundException("The file does not exist.");
		}
		if (info.Length > TorrentMetadata.MaxFileBytes) {
			throw new FormatException("This is not a torrent file (it is far too large).");
		}
		var bytes = await File.ReadAllBytesAsync(path);
		var meta = TorrentMetadata.Parse(bytes);

		var duplicate = TorrentOperations.FindDuplicate(meta.InfoHash, _items, _history.Store);
		if (duplicate?.LiveGid is not null) {
			AlreadyAdded(meta.Name, duplicate.LiveGid);
			return false;
		}

		var note = duplicate?.Past is { } past ? Loc.F("Torrent_AddedBefore", "You added this torrent before ({0:d}).", past.AddedUtc.ToLocalTime()) : null;
		var choice = await AskAsync(new TorrentPrompt(meta.Name, meta.Files, folder, FolderEditable: true, note))
			?? DefaultChoice(folder, meta.Files);
		if (choice.Files.Count == 0) {
			return false; // cancelled
		}

		var gid = await _torrents.AddTorrentAsync(bytes, new TorrentAddRequest(choice.Folder, choice.Files, meta.Files.Count, choice.Seeding, choice.Allocation, extra));
		_added[gid] = DateTimeOffset.Now;
		SaveSettings(s => s.LastUsedFolder = choice.Folder);
		return true;
	}

	private async Task AddMetalinkFileAsync(string path, string folder, AddOptions? extra = null) {
		var info = new FileInfo(path);
		if (!info.Exists) {
			throw new FileNotFoundException("The file does not exist.");
		}
		if (info.Length > TorrentMetadata.MaxFileBytes) {
			throw new FormatException("This does not look like a metalink file (it is far too large).");
		}
		foreach (var gid in await _torrents.AddMetalinkAsync(await File.ReadAllBytesAsync(path), folder, extra)) {
			_added[gid] = DateTimeOffset.Now;
		}
	}

	private async Task<bool> AddMagnetAsync(string magnet, string folder, AddOptions? extra = null) {
		if (MagnetLink.TryGetInfoHash(magnet, out var hash)
			&& TorrentOperations.FindDuplicate(hash, _items, null) is { LiveGid: { } existing }) {
			AlreadyAdded(MagnetLink.DisplayName(magnet) ?? hash, existing);
			return false;
		}
		var gid = await _torrents.AddMagnetAsync(magnet, folder);
		_added[gid] = DateTimeOffset.Now;
		_ = ResolveMagnetAsync(gid, folder, extra);
		return true;
	}

	/// <summary>Runs after a magnet was added: waits for its metadata, asks which files to download, then starts it.</summary>
	private async Task ResolveMagnetAsync(string metadataGid, string folder, AddOptions? extra = null) {
		string? followerGid = null;
		try {
			var resolved = await _torrents.WaitForMetadataAsync(metadataGid);
			if (resolved is null) {
				return; // failed (the row shows the error) or removed by the user
			}
			followerGid = resolved.Gid;
			var files = resolved.Files.Select(f => new TorrentFileEntry(f.Index, RelativeName(f.Path, folder, resolved.Name, resolved.Files.Count), f.Length)).ToList();
			var choice = await AskAsync(new TorrentPrompt(resolved.Name, files, folder, FolderEditable: false, Note: null))
				?? DefaultChoice(folder, files);
			if (choice.Files.Count == 0) {
				await _torrents.DiscardAsync(resolved.Gid);
				await ForgetAsync(metadataGid);
				return;
			}
			await _torrents.StartAsync(resolved.Gid, new TorrentAddRequest(folder, choice.Files, files.Count, choice.Seeding, choice.Allocation, extra));
			await ForgetAsync(metadataGid);
		}
		catch (Exception ex) when (ex is not OperationCanceledException) {
			// Typically the engine stopped while we waited; the paused follower (if any) stays in aria2's session.
			_logger.LogWarning(ex, "Finishing magnet {Gid} failed (follower {Follower})", metadataGid, followerGid);
			_notices.Post(new Notice(Loc.S("Torrent_MagnetFailed", "Could not finish adding a magnet link"), ex.Message, InfoBarSeverity.Warning));
		}
	}

	private async Task ForgetAsync(string gid) {
		if (_engine.Rpc is { } rpc) {
			await rpc.RemoveResultAsync([gid]);
		}
	}

	private async Task<TorrentChoice?> AskAsync(TorrentPrompt prompt) =>
		PromptTorrent is { } ask ? await ask(prompt) : null;

	private static TorrentChoice DefaultChoice(string folder, IReadOnlyList<TorrentFileEntry> files) =>
		new(folder, files.Select(f => f.Index).ToList(), SeedingOverride.Default, AllocationChoice.Default);

	private void AlreadyAdded(string name, string gid) {
		_notices.Post(new Notice(Loc.S("Torrent_AlreadyTitle", "Already in the list"), Loc.F("Torrent_AlreadyBody", "{0} is already being downloaded.", name), InfoBarSeverity.Informational));
		_logger.LogInformation("Torrent {Name} already present as {Gid}", name, gid);
	}

	/// <summary>The file's path relative to the torrent, as the .torrent lists it (aria2 reports absolute paths, with the torrent's folder in front for multi-file torrents).</summary>
	private static string RelativeName(string path, string folder, string torrentName, int fileCount) {
		var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
		var root = torrentName + "/";
		return fileCount > 1 && relative.StartsWith(root, StringComparison.Ordinal) ? relative[root.Length..] : relative;
	}

	private readonly HashSet<string> _spaceChecked = [];

	/// <summary>Warns once per download, as soon as its size is known, when the folder cannot hold the rest of it.</summary>
	private void CheckDiskSpace(IReadOnlyList<DownloadItem> items) {
		foreach (var item in items) {
			if (item.Status is not (DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused) || item.IsMetadata
				|| item.TotalBytes <= 0 || item.Dir is null || !_spaceChecked.Add(item.Gid)) {
				continue;
			}
			var needed = item.TotalBytes - item.CompletedBytes;
			if (!DiskSpace.Fits(needed, item.Dir)) {
				_notices.Post(new Notice(Loc.S("Torrent_NoSpaceTitle", "Not enough disk space"),
					Loc.F("Torrent_NoSpaceBody", "{0} needs {1} more, but {2} is free in {3}.", item.Name, ByteFormat.Size(needed), ByteFormat.Size(DiskSpace.FreeBytes(item.Dir) ?? 0), item.Dir),
					InfoBarSeverity.Warning));
			}
		}
		_spaceChecked.IntersectWith(items.Select(i => i.Gid));
	}

	public Task RecheckSelectedAsync() => Run(async () => {
		foreach (var item in SelectedItems().Where(DownloadCapabilities.CanRecheck)) {
			await _torrents.RecheckAsync(item.Gid);
		}
	});

	public Task SetSeedingSelectedAsync(SeedingOverride seeding) => Run(async () => {
		foreach (var item in SelectedItems().Where(DownloadCapabilities.CanChangeSeeding)) {
			await _torrents.ApplySeedingAsync(item.Gid, seeding);
		}
	});

	/// <summary>The window with files, peers, trackers and pieces of the single selected torrent, or null when that is not what is selected.</summary>
	public TorrentDetailViewModel? CreateDetailViewModel() =>
		SingleSelected() is { IsTorrent: true, IsMetadata: false } item
			? new TorrentDetailViewModel(_engine, item.Gid, _logger)
			: null;

	private string? _pendingExternal;

	/// <summary>Raised when torrent files or magnet links arrive from outside (double-click in Explorer, a link in a browser).</summary>
	public event EventHandler? ExternalAddRequested;

	public void RequestExternalAdd(IEnumerable<string> items) {
		_pendingExternal = string.Join("\r\n", items.Select(i => i.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? i : "\"" + i + "\""));
		ExternalAddRequested?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>The text for the Add dialog from the last <see cref="RequestExternalAdd"/>, once.</summary>
	public string? TakePendingExternalText() {
		var text = _pendingExternal;
		_pendingExternal = null;
		return text;
	}
}
