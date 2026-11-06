using Aria2NET.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria2NET.ViewModels;

/// <summary>One checkbox row of the torrent file picker.</summary>
public sealed partial class TorrentFileChoice(int index, string path, long length) : ObservableObject {
	public int Index { get; } = index;
	public string Path { get; } = path;
	public long Length { get; } = length;
	public string SizeText { get; } = ByteFormat.Size(length);

	[ObservableProperty]
	public partial bool IsSelected { get; set; } = true;
}
