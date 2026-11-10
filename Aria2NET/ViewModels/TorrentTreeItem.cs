using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Aria2NET.ViewModels;

/// <summary>
/// One row of the torrent file picker. The tree itself lives in Core; this wrapper is what the list binds to.
/// The picker is a flat list with indentation and expand arrows (not a TreeView): items handed to a TreeViewNode cannot be
/// cast back by the generated binding code, while items in a list can.
/// </summary>
public sealed partial class TorrentTreeItem : ObservableObject {
	private const double IndentPerLevel = 20;

	private readonly FileTreeNode _node;
	private readonly Action _changed;
	private readonly Action<TorrentTreeItem> _expandedChanged;
	private bool _isExpanded;

	public TorrentTreeItem(FileTreeNode node, int depth, Action changed, Action<TorrentTreeItem> expandedChanged) {
		_node = node;
		_changed = changed;
		_expandedChanged = expandedChanged;
		Depth = depth;
		Children = node.Children.Select(c => new TorrentTreeItem(c, depth + 1, changed, expandedChanged)).ToList();
		node.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsChecked));
	}

	public int Depth { get; }

	public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);

	public string Name => _node.Name;

	public string Glyph => _node.IsFolder ? "" : "";

	public string SizeText => ByteFormat.Size(_node.Length);

	public bool? IsChecked => _node.IsChecked;

	public List<TorrentTreeItem> Children { get; }

	public Visibility ChevronVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

	public string ChevronGlyph => _isExpanded ? "" : "";

	public bool IsExpanded => _isExpanded;

	/// <summary>The box has three states, but a click only ever means "all" or "none": a partly chosen folder becomes all.</summary>
	public void Toggle() {
		_node.IsChecked = _node.IsChecked != true;
		_changed();
	}

	public void ToggleExpand() {
		SetExpanded(!_isExpanded);
		_expandedChanged(this);
	}

	/// <summary>Sets the state without telling the list; the caller rebuilds it.</summary>
	public void SetExpanded(bool value) {
		if (_isExpanded != value) {
			_isExpanded = value;
			OnPropertyChanged(nameof(ChevronGlyph));
		}
	}

	public void SetExpandedAll(bool value) {
		if (Children.Count > 0) {
			SetExpanded(value);
		}
		foreach (var child in Children) {
			child.SetExpandedAll(value);
		}
	}

	/// <summary>This row's descendants that are showing: the children of every expanded folder on the way down.</summary>
	public IEnumerable<TorrentTreeItem> VisibleDescendants() {
		if (!_isExpanded) {
			yield break;
		}
		foreach (var child in Children) {
			yield return child;
			foreach (var below in child.VisibleDescendants()) {
				yield return below;
			}
		}
	}
}
