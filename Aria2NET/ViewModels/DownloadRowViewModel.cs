using System.Diagnostics;
using Aria2NET.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Aria2NET.ViewModels;

/// <summary>One row of the downloads list. Updated in place from poll snapshots.</summary>
public sealed partial class DownloadRowViewModel : ObservableObject {
	private DownloadItem _item;

	public string Gid { get; }

	[ObservableProperty]
	public partial string Name { get; private set; } = "";

	[ObservableProperty]
	public partial string SizeText { get; private set; } = "";

	[ObservableProperty]
	public partial double Progress { get; private set; }

	[ObservableProperty]
	public partial bool IsIndeterminate { get; private set; }

	[ObservableProperty]
	public partial string SpeedText { get; private set; } = "";

	[ObservableProperty]
	public partial string EtaText { get; private set; } = "";

	[ObservableProperty]
	public partial string StatusText { get; private set; } = "";

	/// <summary>Error details for the status tooltip; empty when there is none.</summary>
	[ObservableProperty]
	public partial string StatusTip { get; private set; } = "";

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
		StatusTip = item.Status == DownloadStatus.Error ? item.ErrorMessage ?? "" : "";
	}

	[RelayCommand]
	private void OpenFolder() {
		if (string.IsNullOrEmpty(_item.Dir)) {
			return;
		}
		var file = Path.Combine(_item.Dir, _item.Name);
		var args = File.Exists(file) ? $"/select,\"{file}\"" : $"\"{_item.Dir}\"";
		Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
	}

	[RelayCommand]
	private void CopyUrl() {
		if (string.IsNullOrEmpty(_item.FirstUri)) {
			return;
		}
		var package = new DataPackage();
		package.SetText(_item.FirstUri);
		Clipboard.SetContent(package);
	}
}
