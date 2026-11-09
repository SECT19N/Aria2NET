using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria2NET.ViewModels;

/// <summary>
/// One row of the torrent file picker. The tree itself lives in Core; this wrapper is what the XAML template binds to,
/// because a template cannot bind to a type from another assembly (the generated code fails to cast it).
/// </summary>
public sealed partial class TorrentTreeItem : ObservableObject {
	private readonly FileTreeNode _node;
	private readonly Action _changed;

	public TorrentTreeItem(FileTreeNode node, Action changed) {
		_node = node;
		_changed = changed;
		Children = node.Children.Select(c => new TorrentTreeItem(c, changed)).ToList();
		node.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsChecked));
	}

	public string Name => _node.Name;

	public string Glyph => _node.IsFolder ? "" : "";

	public string SizeText => ByteFormat.Size(_node.Length);

	public bool? IsChecked => _node.IsChecked;

	public IReadOnlyList<TorrentTreeItem> Children { get; }

	/// <summary>The box has three states, but a click only ever means "all" or "none": a partly chosen folder becomes all.</summary>
	public void Toggle() {
		_node.IsChecked = _node.IsChecked != true;
		_changed();
	}
}
