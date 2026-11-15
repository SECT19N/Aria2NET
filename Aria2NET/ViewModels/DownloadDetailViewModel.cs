using System.Collections.ObjectModel;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using Aria2NET.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Aria2NET.ViewModels;

/// <summary>One file of a download. Updated in place so the list keeps its scroll position while the numbers change.</summary>
public sealed partial class DetailFileRow(int index, string path, string fullPath) : ObservableObject {
	/// <summary>1-based, as aria2's select-file counts.</summary>
	public int Index { get; } = index;

	/// <summary>Where the file is on disk, for Open and Show in folder.</summary>
	public string FullPath { get; } = fullPath;

	public string Path { get; } = path;

	/// <summary>More than one file: only then is there anything to choose between.</summary>
	[ObservableProperty]
	public partial bool ShowChoice { get; set; }

	/// <summary>The download is in a state where aria2 still takes a different choice of files.</summary>
	[ObservableProperty]
	public partial bool CanChoose { get; set; }

	[ObservableProperty]
	public partial string Size { get; set; } = "";

	/// <summary>0 to 100.</summary>
	[ObservableProperty]
	public partial double Progress { get; set; }

	/// <summary>The percentage as text, or "skipped" for a file the user left out.</summary>
	[ObservableProperty]
	public partial string Done { get; set; } = "";

	[ObservableProperty]
	public partial bool Selected { get; set; } = true;
}

public sealed record DetailPeerRow(string Address, string Client, string Country, string CountryTip, string Down, string Up, string Progress, string Flags);

public sealed record DetailTrackerRow(string Url, string Tier);

/// <summary>Live data of the details pane for one download (a torrent or a plain one). Refreshes itself once a second until <see cref="Stop"/>.</summary>
public sealed partial class DownloadDetailViewModel : ObservableObject {
	private static readonly Lazy<Task<GeoIpResolver>> Geo = new(() => Task.Run(() => GeoIpResolver.Load(App.Services.GetService(typeof(AppPaths)) is AppPaths p ? p.GeoIpFile : "")));

	private readonly IAria2Engine _engine;
	private readonly ILogger _logger;
	private readonly DispatcherQueueTimer _timer;
	private bool _busy;
	private bool _stopped;

	public string Gid { get; }

	public ObservableCollection<DetailFileRow> Files { get; } = [];
	public ObservableCollection<DetailPeerRow> Peers { get; } = [];
	public ObservableCollection<DetailTrackerRow> Trackers { get; } = [];

	/// <summary>Peers, trackers and pieces only exist for torrents.</summary>
	[ObservableProperty]
	public partial bool IsTorrent { get; private set; }

	[ObservableProperty]
	public partial string Downloaded { get; private set; } = Loc.S("Detail_Loading", "Loading…");

	[ObservableProperty]
	public partial string Speed { get; private set; } = "";

	[ObservableProperty]
	public partial string Uploaded { get; private set; } = "";

	[ObservableProperty]
	public partial string Connections { get; private set; } = "";

	[ObservableProperty]
	public partial string SavedTo { get; private set; } = "";

	/// <summary>Where a plain download comes from; empty for a torrent.</summary>
	[ObservableProperty]
	public partial string Source { get; private set; } = "";

	[ObservableProperty]
	public partial string InfoHash { get; private set; } = "";

	[ObservableProperty]
	public partial string Created { get; private set; } = "";

	[ObservableProperty]
	public partial string Comment { get; private set; } = "";

	[ObservableProperty]
	public partial string PieceInfo { get; private set; } = "";

	[ObservableProperty]
	public partial Brush? HaveBrush { get; private set; }

	[ObservableProperty]
	public partial Brush? AvailabilityBrush { get; private set; }

	[ObservableProperty]
	public partial string FilesHeader { get; private set; } = Loc.S("Detail_Files", "Files");

	[ObservableProperty]
	public partial string PeersHeader { get; private set; } = Loc.S("Detail_Peers", "Peers");

	[ObservableProperty]
	public partial string Problem { get; private set; } = "";

	public bool HasGeoIp { get; private set; }

	private readonly TorrentOperations _torrents;
	private readonly NoticeCenter _notices;
	private bool _editable;
	private bool _choosing;

	public DownloadDetailViewModel(IAria2Engine engine, TorrentOperations torrents, NoticeCenter notices, string gid, ILogger logger) {
		_engine = engine;
		_torrents = torrents;
		_notices = notices;
		Gid = gid;
		_logger = logger;
		_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_timer.Interval = TimeSpan.FromSeconds(1);
		_timer.Tick += async (_, _) => await RefreshAsync();
	}

	public async Task StartAsync() {
		await RefreshAsync();
		if (!_stopped) {
			_timer.Start();
		}
	}

	public void Stop() {
		_stopped = true;
		_timer.Stop();
	}

	private async Task RefreshAsync() {
		if (_busy || _stopped || _engine.Rpc is not { } rpc) {
			return;
		}
		_busy = true;
		try {
			GeoIpResolver geo = await Geo.Value;
			HasGeoIp = geo.RangeCount > 0;
			TorrentDetailSnapshot snapshot = await TorrentDetailLoader.LoadAsync(rpc, Gid, geo);
			if (!_stopped) {
				Apply(snapshot);
				Problem = "";
			}
		} catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or TaskCanceledException) {
			// The download was removed, or the engine is restarting; keep showing the last data.
			_logger.LogDebug(ex, "Refreshing download details failed");
			Problem = ex is Aria2RpcException ? Loc.S("Detail_Gone", "This download is no longer in the list.") : Loc.S("Detail_WaitingEngine", "Waiting for the download engine…");
		} finally {
			_busy = false;
		}
	}

	private void Apply(TorrentDetailSnapshot s) {
		IsTorrent = s.IsTorrent;
		Downloaded = s.TotalBytes > 0
			? Loc.F("Detail_Downloaded", "{0} of {1} ({2:0.#}%)", ByteFormat.Size(s.CompletedBytes), ByteFormat.Size(s.TotalBytes), s.Progress)
			: ByteFormat.Size(s.CompletedBytes);
		Speed = s.IsTorrent
			? Loc.F("Detail_SpeedBoth", "↓ {0}   ↑ {1}", ByteFormat.Speed(s.DownloadSpeed), ByteFormat.Speed(s.UploadSpeed))
			: Loc.F("Detail_SpeedDown", "↓ {0}", ByteFormat.Speed(s.DownloadSpeed));
		Uploaded = s.IsTorrent ? Loc.F("Detail_Uploaded", "{0} · ratio {1:0.00}", ByteFormat.Size(s.UploadedBytes), s.Ratio) : "";
		Connections = s.IsTorrent
			? Loc.F("Detail_Swarm", "{0} seeders · {1} connections", s.SeedersInSwarm, s.Connections)
			: Loc.F("Detail_Connections", "{0} connections", s.Connections);
		SavedTo = s.Directory;
		Source = s.IsTorrent ? "" : s.Uri ?? "";
		InfoHash = s.InfoHash;
		Created = s.CreatedAt is { } at ? at.LocalDateTime.ToString("g") : "";
		Comment = s.Comment ?? "";
		PieceInfo = s.PieceCount > 0 ? Loc.F("Detail_Pieces", "{0:N0} pieces of {1}", s.PieceCount, ByteFormat.Size(s.PieceLength)) : Loc.S("Detail_NoPieces", "No piece information yet");
		FilesHeader = Loc.F("Detail_FilesCount", "Files ({0})", s.Files.Count);
		PeersHeader = Loc.F("Detail_PeersCount", "Peers ({0})", s.Peers.Count);

		ApplyFiles(s);
		Replace(Peers, s.Peers.OrderByDescending(p => p.DownloadSpeed + p.UploadSpeed).ThenBy(p => p.Address).Select(p => new DetailPeerRow(
			p.Address, p.Client, p.Country ?? "", GeoIpResolver.CountryName(p.Country),
			p.DownloadSpeed > 0 ? ByteFormat.Speed(p.DownloadSpeed) : "", p.UploadSpeed > 0 ? ByteFormat.Speed(p.UploadSpeed) : "",
			$"{p.Progress:0}%", Flags(p))));
		Replace(Trackers, s.Trackers.Select(t => new DetailTrackerRow(t.Url, Loc.F("Detail_Tier", "Tier {0}", t.Tier))));

		HaveBrush = Bar(s.HaveBar, Color.FromArgb(0xFF, 0x2E, 0x9E, 0x4F), Color.FromArgb(0x40, 0x80, 0x80, 0x80));
		// Availability: grey = no peer has it, teal = every connected peer has it.
		AvailabilityBrush = Bar(s.AvailabilityBar, Color.FromArgb(0xFF, 0x0F, 0x9D, 0x8A), Color.FromArgb(0x40, 0x80, 0x80, 0x80));
	}

	/// <summary>Rebuilds the file rows only when the set of files changes; otherwise just moves their numbers.</summary>
	private void ApplyFiles(TorrentDetailSnapshot s) {
		List<string> names = s.Files.Select(f => RelativeName(f.Path, s.Directory)).ToList();
		if (!Files.Select(f => f.Path).SequenceEqual(names)) {
			Files.Clear();
			for (int i = 0; i < names.Count; i++) {
				Files.Add(new DetailFileRow(s.Files[i].Index, names[i], s.Files[i].Path));
			}
		}
		for (int i = 0; i < s.Files.Count; i++) {
			DownloadFile file = s.Files[i];
			double percent = file.Length > 0 ? Math.Min(100.0, file.CompletedLength * 100.0 / file.Length) : 0;
			DetailFileRow row = Files[i];
			row.Size = file.Length > 0 ? ByteFormat.Size(file.Length) : "";
			row.ShowChoice = Files.Count > 1;
			row.CanChoose = _editable && Files.Count > 1;
			if (_choosing) {
				continue; // the user's choice is on its way to aria2; the old answer must not flip the boxes back
			}
			row.Selected = file.Selected;
			row.Progress = file.Selected ? percent : 0;
			row.Done = !file.Selected ? Loc.S("Detail_Skipped", "skipped") : file.Length > 0 ? $"{percent:0.#}%" : "";
		}
	}

	/// <summary>Whether the download still takes a different choice of files (it is a running or waiting torrent, not a finished one).</summary>
	public void SetEditable(bool editable) {
		if (_editable == editable) {
			return;
		}
		_editable = editable;
		foreach (DetailFileRow row in Files) {
			row.CanChoose = _editable && Files.Count > 1;
		}
	}

	/// <summary>
	/// Sends the files that are ticked to aria2. <paramref name="changed"/> is the row the user just touched: it is put back when nothing
	/// would be left to download.
	/// </summary>
	public async Task ChooseAsync(DetailFileRow changed) {
		if (_choosing || !_editable) {
			return;
		}
		List<int> wanted = Files.Where(f => f.Selected).Select(f => f.Index).ToList();
		if (wanted.Count == 0) {
			changed.Selected = true;
			_notices.Post(new Notice(Loc.S("Detail_FilesTitle", "Could not change the files"), Loc.S("Detail_KeepOne", "At least one file has to stay selected."), InfoBarSeverity.Informational));
			return;
		}
		_choosing = true;
		try {
			await _torrents.SelectFilesAsync(Gid, wanted, Files.Count);
		} catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or InvalidOperationException or ArgumentException or TaskCanceledException) {
			_logger.LogWarning(ex, "Changing the files of {Gid} failed", Gid);
			_notices.Post(new Notice(Loc.S("Detail_FilesTitle", "Could not change the files"), ex.Message, InfoBarSeverity.Warning));
		} finally {
			_choosing = false;
		}
		await RefreshAsync(); // show what aria2 really has now, also after a failure
	}

	/// <summary>Downloads only this file.</summary>
	public Task ChooseOnlyAsync(DetailFileRow only) {
		foreach (DetailFileRow row in Files) {
			row.Selected = ReferenceEquals(row, only);
		}
		return ChooseAsync(only);
	}

	/// <summary>Downloads every file.</summary>
	public Task ChooseAllAsync() {
		foreach (DetailFileRow row in Files) {
			row.Selected = true;
		}
		return Files.Count > 0 ? ChooseAsync(Files[0]) : Task.CompletedTask;
	}

	private static string Flags(TorrentPeer p) => string.Join(' ', new[] {
		p.IsSeeder ? "seed" : null,
		p.DownloadSpeed > 0 ? "↓" : null,
		p.UploadSpeed > 0 ? "↑" : null,
		p.ChokesUs ? "choked" : null,
	}.Where(x => x is not null));

	private static string RelativeName(string path, string dir) {
		if (string.IsNullOrEmpty(path)) {
			return "";
		}
		string relative = string.IsNullOrEmpty(dir) ? path : System.IO.Path.GetRelativePath(dir, path);
		return relative.Replace('\\', '/');
	}

	private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) {
		List<T> list = items.ToList();
		if (target.SequenceEqual(list)) {
			return;
		}
		target.Clear();
		foreach (T item in list) {
			target.Add(item);
		}
	}

	/// <summary>A bar whose colour per slice goes from <paramref name="empty"/> (0) to <paramref name="full"/> (1), with hard edges between slices.</summary>
	private static LinearGradientBrush Bar(double[] values, Color full, Color empty) {
		LinearGradientBrush brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
		if (values.Length == 0) {
			brush.GradientStops.Add(new GradientStop { Color = empty, Offset = 0 });
			brush.GradientStops.Add(new GradientStop { Color = empty, Offset = 1 });
			return brush;
		}
		for (int i = 0; i < values.Length; i++) {
			double v = Math.Clamp(values[i], 0, 1);
			Color color = Color.FromArgb(
				(byte) (empty.A + (full.A - empty.A) * v), (byte) (empty.R + (full.R - empty.R) * v),
				(byte) (empty.G + (full.G - empty.G) * v), (byte) (empty.B + (full.B - empty.B) * v));
			brush.GradientStops.Add(new GradientStop { Color = color, Offset = (double) i / values.Length });
			brush.GradientStops.Add(new GradientStop { Color = color, Offset = (i + 1.0) / values.Length });
		}
		return brush;
	}
}
