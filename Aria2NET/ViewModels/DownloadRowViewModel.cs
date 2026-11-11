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
	}

	private static string ErrorTip(DownloadItem item) {
		var friendly = Aria2Errors.Describe(item.ErrorCode, item.ErrorMessage);
		return !string.IsNullOrWhiteSpace(item.ErrorMessage) && !string.Equals(friendly, item.ErrorMessage, StringComparison.Ordinal)
			? $"{friendly}\n({item.ErrorMessage})"
			: friendly;
	}
}
