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
		IsIndeterminate = item.Status == DownloadStatus.Active && item.TotalBytes == 0;
		ProgressBrush = item.Status switch {
			DownloadStatus.Paused => PausedBrush,
			DownloadStatus.Complete => CompleteBrush,
			DownloadStatus.Error or DownloadStatus.Removed => ErrorBrush,
			_ => ActiveBrush,
		};
		SizeText = item.TotalBytes > 0
			? item.Status == DownloadStatus.Complete ? ByteFormat.Size(item.TotalBytes) : $"{ByteFormat.Size(item.CompletedBytes)} / {ByteFormat.Size(item.TotalBytes)}"
			: item.CompletedBytes > 0 ? ByteFormat.Size(item.CompletedBytes) : "—";
		var active = item.Status == DownloadStatus.Active;
		SpeedText = active ? ByteFormat.Speed(item.SpeedBytesPerSec) : "";
		EtaText = active ? TimeFormat.Eta(item.Eta) : "";
		StatusText = item.Status switch {
			DownloadStatus.Active => "Downloading",
			DownloadStatus.Waiting => "Queued",
			DownloadStatus.Paused => "Paused",
			DownloadStatus.Error => "Error",
			DownloadStatus.Complete => "Complete",
			_ => "Removed",
		};
		StatusTip = item.Status == DownloadStatus.Error ? ErrorTip(item) : "";
		AccessibleName = $"{item.Name}, {StatusText}, {item.Progress:0} percent";
	}

	private static string ErrorTip(DownloadItem item) {
		var friendly = Aria2Errors.Describe(item.ErrorCode, item.ErrorMessage);
		return !string.IsNullOrWhiteSpace(item.ErrorMessage) && !string.Equals(friendly, item.ErrorMessage, StringComparison.Ordinal)
			? $"{friendly}\n({item.ErrorMessage})"
			: friendly;
	}
}
