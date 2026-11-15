using Aria2NET.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace Aria2NET.ViewModels;

/// <summary>One row of the downloads list. Updated in place from poll snapshots.</summary>
public sealed partial class DownloadRowViewModel : ObservableObject {
	private static readonly Brush ActiveBrush = new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
	private static readonly Brush PausedBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x8A, 0x8A, 0x8A));
	private static readonly Brush CompleteBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x2E, 0x9E, 0x4F));
	private static readonly Brush ErrorBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xD1, 0x34, 0x38));
	private static readonly Brush SeedingBrush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x0F, 0x9D, 0x8A));

	private DownloadItem _item;

	public string Gid { get; }

	public DownloadItem Item => _item;

	[ObservableProperty]
	public partial string Name { get; private set; } = "";

	[ObservableProperty]
	public partial string SizeText { get; private set; } = "";

	[ObservableProperty]
	public partial double Progress { get; private set; }

	[ObservableProperty]
	public partial bool IsIndeterminate { get; private set; }

	/// <summary>Blue while downloading, gray paused, green complete, red failed.</summary>
	[ObservableProperty]
	public partial Brush ProgressBrush { get; private set; } = ActiveBrush;

	[ObservableProperty]
	public partial string SpeedText { get; private set; } = "";

	[ObservableProperty]
	public partial string EtaText { get; private set; } = "";

	[ObservableProperty]
	public partial string StatusText { get; private set; } = "";

	/// <summary>Error details for the status tooltip; empty when there is none.</summary>
	[ObservableProperty]
	public partial string StatusTip { get; private set; } = "";

	/// <summary>Name read out by screen readers: color is never the only carrier of the state.</summary>
	[ObservableProperty]
	public partial string AccessibleName { get; private set; } = "";

	/// <summary>The dimmed second line: where it is saved and when it was added or finished.</summary>
	[ObservableProperty]
	public partial string SubText { get; private set; } = "";

	/// <summary>Speed and time left as one text, shown on the second line when the window is too narrow for their columns.</summary>
	[ObservableProperty]
	public partial string CompactText { get; private set; } = "";

	/// <summary>Everything about the download in a few lines, for the tooltip.</summary>
	[ObservableProperty]
	public partial string ToolTipText { get; private set; } = "";

	/// <summary>Icon and words of the one-click action on the row (pause, resume or retry); none when nothing fits.</summary>
	[ObservableProperty]
	public partial string PrimaryGlyph { get; private set; } = "";

	[ObservableProperty]
	public partial string PrimaryTip { get; private set; } = "";

	[ObservableProperty]
	public partial bool HasPrimary { get; private set; }

	[ObservableProperty]
	public partial bool HasFolder { get; private set; }

	private DateTimeOffset? _added;
	private DateTimeOffset? _finished;

	/// <summary>How often the add time was looked up without an answer; the list stops asking after a few tries.</summary>
	public int TimeLookups { get; set; }

	public bool HasAdded => _added is not null;

	/// <summary>The add time (or, for a finished download, the finish time) is still unknown.</summary>
	public bool NeedsTimes => _added is null || (_item.Status is DownloadStatus.Complete or DownloadStatus.Seeding && _finished is null);

	/// <summary>Called by the list once it knows when the download was added (and finished).</summary>
	public void SetTimes(DateTimeOffset? added, DateTimeOffset? finished) {
		if (_added == added && _finished == finished) {
			return;
		}
		_added = added;
		_finished = finished;
		Describe(_item);
	}

	private void Describe(DownloadItem item) {
		var now = DateTimeOffset.Now;
		var done = item.Status is DownloadStatus.Complete or DownloadStatus.Seeding;
		var when = done && _finished is { } f ? Loc.F("Row_Finished", "Finished {0}", RowText.When(f, now))
			: _added is { } a ? Loc.F("Row_Added", "Added {0}", RowText.When(a, now))
			: "";
		SubText = string.Join(" · ", new[] { RowText.ShortenPath(item.Dir), when }.Where(s => s.Length > 0));
		CompactText = string.Join(" · ", new[] { SpeedText, EtaText }.Where(s => s.Length > 0));
		HasFolder = !string.IsNullOrEmpty(item.Dir);

		(PrimaryGlyph, PrimaryTip) = item.Status switch {
			DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Seeding => ("", Loc.S("Row_Pause", "Pause")),
			DownloadStatus.Paused => ("", Loc.S("Row_Resume", "Resume")),
			DownloadStatus.Error or DownloadStatus.Removed when DownloadCapabilities.CanRetry(item) => ("", Loc.S("Row_Retry", "Retry")),
			_ => ("", ""),
		};
		HasPrimary = PrimaryGlyph.Length > 0;

		var lines = new List<string> { item.Name };
		if (!string.IsNullOrEmpty(item.Path) && !(item.IsTorrent && item.FileCount > 1)) {
			lines.Add(Loc.F("Row_TipLocation", "Location: {0}", item.Path));
		} else if (!string.IsNullOrEmpty(item.Dir)) {
			lines.Add(Loc.F("Row_TipLocation", "Location: {0}", item.Dir));
		}
		if (_added is { } added) {
			lines.Add(Loc.F("Row_TipAdded", "Added: {0}", RowText.When(added, now)));
		}
		if (_finished is { } finished && done) {
			lines.Add(Loc.F("Row_TipFinished", "Finished: {0}", RowText.When(finished, now)));
		}
		lines.Add(Loc.F("Row_TipSize", "Size: {0}", SizeText));
		if (item.IsTorrent && !string.IsNullOrEmpty(item.InfoHash)) {
			lines.Add(Loc.F("Row_TipHash", "Info hash: {0}", item.InfoHash));
		} else if (!string.IsNullOrEmpty(item.FirstUri)) {
			lines.Add(Loc.F("Row_TipSource", "Source: {0}", item.FirstUri));
		}
		if (item.Status == DownloadStatus.Active) {
			lines.Add(Loc.F("Row_TipConnections", "Connections: {0}", item.Connections));
		}
		if (StatusTip.Length > 0) {
			lines.Add(StatusTip);
		}
		ToolTipText = string.Join("\n", lines);
	}

	public DownloadRowViewModel(DownloadItem item) {
		Gid = item.Gid;
		_item = item;
		Apply(item);
	}

	public void Update(DownloadItem item) {
		if (item == _item) {
			return;
		}
		_item = item;
		Apply(item);
	}

	private void Apply(DownloadItem item) {
		Name = item.Name;
		Progress = item.Progress;
		IsIndeterminate = item.Status == DownloadStatus.Active && (item.TotalBytes == 0 || item.IsFetchingMetadata);
		ProgressBrush = item.Status switch {
			DownloadStatus.Paused => PausedBrush,
			DownloadStatus.Complete => CompleteBrush,
			DownloadStatus.Seeding => SeedingBrush,
			DownloadStatus.Error or DownloadStatus.Removed => ErrorBrush,
			_ => ActiveBrush,
		};
		SizeText = item.TotalBytes > 0
			? item.Status is DownloadStatus.Complete or DownloadStatus.Seeding ? ByteFormat.Size(item.TotalBytes) : $"{ByteFormat.Size(item.CompletedBytes)} / {ByteFormat.Size(item.TotalBytes)}"
			: item.CompletedBytes > 0 ? ByteFormat.Size(item.CompletedBytes) : "—";
		var active = item.Status == DownloadStatus.Active;
		SpeedText = active ? ByteFormat.Speed(item.SpeedBytesPerSec) : item.Status == DownloadStatus.Seeding ? "↑ " + ByteFormat.Speed(item.UploadSpeedBytesPerSec) : "";
		EtaText = active ? TimeFormat.Eta(item.Eta) : "";
		StatusText = item.Status switch {
			DownloadStatus.Active => item.IsFetchingMetadata ? Loc.S("Status_FetchingMetadata", "Fetching metadata…") : Loc.S("Status_Downloading", "Downloading"),
			DownloadStatus.Seeding => Loc.S("Status_Seeding", "Seeding"),
			DownloadStatus.Waiting => Loc.S("Status_Queued", "Queued"),
			DownloadStatus.Paused => Loc.S("Status_Paused", "Paused"),
			DownloadStatus.Error => Loc.S("Status_Error", "Error"),
			DownloadStatus.Complete => Loc.S("Status_Complete", "Complete"),
			_ => Loc.S("Status_Removed", "Removed"),
		};
		StatusTip = item.Status == DownloadStatus.Error ? ErrorTip(item)
			: item.IsTorrent && item.UploadedBytes > 0 ? Loc.F("Row_Uploaded", "Uploaded {0} · ratio {1:0.00}", ByteFormat.Size(item.UploadedBytes), item.ShareRatio)
			: "";
		AccessibleName = Loc.F("Row_Accessible", "{0}, {1}, {2:0} percent", item.Name, StatusText, item.Progress);
		Describe(item);
	}

	private static string ErrorTip(DownloadItem item) {
		var friendly = Aria2Errors.Describe(item.ErrorCode, item.ErrorMessage);
		return !string.IsNullOrWhiteSpace(item.ErrorMessage) && !string.Equals(friendly, item.ErrorMessage, StringComparison.Ordinal)
			? $"{friendly}\n({item.ErrorMessage})"
			: friendly;
	}
}
