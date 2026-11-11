using System.Collections.ObjectModel;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Aria2NET.ViewModels;

public sealed record DetailFileRow(string Path, string Size, string Done, bool Selected);

public sealed record DetailPeerRow(string Address, string Client, string Country, string CountryTip, string Down, string Up, string Progress, string Flags);

public sealed record DetailTrackerRow(string Url, string Tier);

/// <summary>Live data of the torrent details window. Refreshes itself once a second until <see cref="Stop"/>.</summary>
public sealed partial class TorrentDetailViewModel : ObservableObject {
	private static readonly Lazy<Task<GeoIpResolver>> Geo = new(() => Task.Run(() => GeoIpResolver.Load(App.Services.GetService(typeof(AppPaths)) is AppPaths p ? p.GeoIpFile : "")));

	private readonly IAria2Engine _engine;
	private readonly string _gid;
	private readonly ILogger _logger;
	private readonly DispatcherQueueTimer _timer;
	private bool _busy;

	public ObservableCollection<DetailFileRow> Files { get; } = [];
	public ObservableCollection<DetailPeerRow> Peers { get; } = [];
	public ObservableCollection<DetailTrackerRow> Trackers { get; } = [];

	[ObservableProperty]
	public partial string Title { get; private set; } = "";

	[ObservableProperty]
	public partial string Summary { get; private set; } = "Loading…";

	[ObservableProperty]
	public partial string InfoHash { get; private set; } = "";

	[ObservableProperty]
	public partial string SavedTo { get; private set; } = "";

	[ObservableProperty]
	public partial string Transfer { get; private set; } = "";

	[ObservableProperty]
	public partial string Swarm { get; private set; } = "";

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
	public partial string PeersHeader { get; private set; } = Loc.S("Detail_Peers", "Peers");

	[ObservableProperty]
	public partial string Problem { get; private set; } = "";

	public bool HasGeoIp { get; private set; }

	public TorrentDetailViewModel(IAria2Engine engine, string gid, ILogger logger) {
		_engine = engine;
		_gid = gid;
		_logger = logger;
		_timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		_timer.Interval = TimeSpan.FromSeconds(1);
		_timer.Tick += async (_, _) => await RefreshAsync();
	}

	public async Task StartAsync() {
		await RefreshAsync();
		_timer.Start();
	}

	public void Stop() => _timer.Stop();

	private async Task RefreshAsync() {
		if (_busy || _engine.Rpc is not { } rpc) {
			return;
		}
		_busy = true;
		try {
			var geo = await Geo.Value;
			HasGeoIp = geo.RangeCount > 0;
			Apply(await TorrentDetailLoader.LoadAsync(rpc, _gid, geo));
			Problem = "";
		}
		catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException or TaskCanceledException) {
			// The download was removed, or the engine is restarting; keep showing the last data.
			_logger.LogDebug(ex, "Refreshing torrent details failed");
			Problem = ex is Aria2RpcException ? "This torrent is no longer in the list." : "Waiting for the download engine…";
		}
		finally {
			_busy = false;
		}
	}

	private void Apply(TorrentDetailSnapshot s) {
		Title = s.Name;
		Summary = Loc.F("Detail_Summary", "{0} · {1} of {2} ({3:0.#}%) · ratio {4:0.00}", s.Status, ByteFormat.Size(s.CompletedBytes), ByteFormat.Size(s.TotalBytes), s.Progress, s.Ratio);
		InfoHash = s.InfoHash;
		SavedTo = s.Directory;
		Transfer = Loc.F("Detail_Transfer", "↓ {0}   ↑ {1}   uploaded {2}", ByteFormat.Speed(s.DownloadSpeed), ByteFormat.Speed(s.UploadSpeed), ByteFormat.Size(s.UploadedBytes));
		Swarm = Loc.F("Detail_Swarm", "{0} seeders · {1} connections", s.SeedersInSwarm, s.Connections);
		Created = s.CreatedAt is { } at ? at.LocalDateTime.ToString("g") : "";
		Comment = s.Comment ?? "";
		PieceInfo = s.PieceCount > 0 ? Loc.F("Detail_Pieces", "{0:N0} pieces of {1}", s.PieceCount, ByteFormat.Size(s.PieceLength)) : Loc.S("Detail_NoPieces", "No piece information yet");
		PeersHeader = Loc.F("Detail_PeersCount", "Peers ({0})", s.Peers.Count);

		Replace(Files, s.Files.Select(f => new DetailFileRow(
			RelativeName(f.Path, s.Directory), ByteFormat.Size(f.Length),
			!f.Selected ? Loc.S("Detail_Skipped", "skipped") : f.Length > 0 ? $"{f.CompletedLength * 100.0 / f.Length:0.#}%" : "", f.Selected)));
		Replace(Peers, s.Peers.OrderByDescending(p => p.DownloadSpeed + p.UploadSpeed).ThenBy(p => p.Address).Select(p => new DetailPeerRow(
			p.Address, p.Client, p.Country ?? "", GeoIpResolver.CountryName(p.Country),
			p.DownloadSpeed > 0 ? ByteFormat.Speed(p.DownloadSpeed) : "", p.UploadSpeed > 0 ? ByteFormat.Speed(p.UploadSpeed) : "",
			$"{p.Progress:0}%", Flags(p))));
		Replace(Trackers, s.Trackers.Select(t => new DetailTrackerRow(t.Url, Loc.F("Detail_Tier", "Tier {0}", t.Tier))));

		HaveBrush = Bar(s.HaveBar, Color.FromArgb(0xFF, 0x2E, 0x9E, 0x4F), Color.FromArgb(0x40, 0x80, 0x80, 0x80));
		// Availability: grey = no peer has it, teal = every connected peer has it.
		AvailabilityBrush = Bar(s.AvailabilityBar, Color.FromArgb(0xFF, 0x0F, 0x9D, 0x8A), Color.FromArgb(0x40, 0x80, 0x80, 0x80));
	}

	private static string Flags(TorrentPeer p) => string.Join(' ', new[] {
		p.IsSeeder ? "seed" : null,
		p.DownloadSpeed > 0 ? "↓" : null,
		p.UploadSpeed > 0 ? "↑" : null,
		p.ChokesUs ? "choked" : null,
	}.Where(x => x is not null));

	private static string RelativeName(string path, string dir) {
		var relative = string.IsNullOrEmpty(dir) ? path : System.IO.Path.GetRelativePath(dir, path);
		return relative.Replace('\\', '/');
	}

	private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) {
		var list = items.ToList();
		if (target.SequenceEqual(list)) {
			return;
		}
		target.Clear();
		foreach (var item in list) {
			target.Add(item);
		}
	}

	/// <summary>A bar whose colour per slice goes from <paramref name="empty"/> (0) to <paramref name="full"/> (1), with hard edges between slices.</summary>
	private static LinearGradientBrush Bar(double[] values, Color full, Color empty) {
		var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
		if (values.Length == 0) {
			brush.GradientStops.Add(new GradientStop { Color = empty, Offset = 0 });
			brush.GradientStops.Add(new GradientStop { Color = empty, Offset = 1 });
			return brush;
		}
		for (var i = 0; i < values.Length; i++) {
			var v = Math.Clamp(values[i], 0, 1);
			var color = Color.FromArgb(
				(byte)(empty.A + (full.A - empty.A) * v), (byte)(empty.R + (full.R - empty.R) * v),
				(byte)(empty.G + (full.G - empty.G) * v), (byte)(empty.B + (full.B - empty.B) * v));
			brush.GradientStops.Add(new GradientStop { Color = color, Offset = (double)i / values.Length });
			brush.GradientStops.Add(new GradientStop { Color = color, Offset = (i + 1.0) / values.Length });
		}
		return brush;
	}
}
