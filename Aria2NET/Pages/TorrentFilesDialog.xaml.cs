using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using Aria2NET.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Aria2NET.Pages;

public sealed partial class TorrentFilesDialog : ContentDialog {
	private readonly TorrentFileTree _tree;
	private bool _ready;

	public TorrentFilesDialog(TorrentPrompt prompt) {
		_tree = TorrentFileTree.Build(prompt.Files);
		InitializeComponent();

		NameText.Text = prompt.Name;
		ToolTipService.SetToolTip(NameText, prompt.Name);
		TotalText.Text = $"{_tree.TotalCount} {(_tree.TotalCount == 1 ? "file" : "files")} · {ByteFormat.Size(_tree.TotalBytes)}";
		if (prompt.Note is { Length: > 0 } note) {
			NoteBar.Message = note;
			NoteBar.IsOpen = true;
		}
		BuildTree();
		FolderBox.Text = prompt.Folder;
		FolderBox.IsEnabled = BrowseButton.IsEnabled = prompt.FolderEditable;
		if (!prompt.FolderEditable) {
			ToolTipService.SetToolTip(FolderBox, "The folder was fixed when the magnet link was added.");
		}
		_ready = true;
		Refresh();
	}

	/// <summary>What the user chose. No files means the dialog was cancelled.</summary>
	public TorrentChoice Choice => new(
		FolderBox.Text.Trim(),
		_tree.SelectedIndexes(),
		SeedBox.SelectedIndex switch {
			1 => new SeedingOverride(SeedMode.Forever),
			2 => new SeedingOverride(SeedMode.Ratio, double.IsNaN(RatioBox.Value) ? 1.0 : RatioBox.Value),
			3 => new SeedingOverride(SeedMode.Never),
			_ => SeedingOverride.Default,
		},
		AllocationBox.SelectedIndex switch {
			1 => AllocationChoice.Sparse,
			2 => AllocationChoice.Full,
			_ => AllocationChoice.Default,
		});

	private void Refresh() {
		if (!_ready) {
			return;
		}
		var count = _tree.SelectedCount;
		var bytes = _tree.SelectedBytes;
		SummaryText.Text = $"{count} of {_tree.TotalCount} selected · {ByteFormat.Size(bytes)}";

		var folder = FolderBox.Text.Trim();
		var folderOk = folder.Length > 0 && Path.IsPathFullyQualified(folder);
		var free = folderOk ? DiskSpace.FreeBytes(folder) : null;
		if (free is { } f) {
			var fits = bytes <= f;
			SpaceText.Text = fits ? $"{ByteFormat.Size(f)} free in this folder." : $"Not enough space: this needs {ByteFormat.Size(bytes)} but only {ByteFormat.Size(f)} is free.";
			SpaceText.Foreground = fits ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
		}
		else {
			SpaceText.Text = "";
		}
		IsPrimaryButtonEnabled = count > 0 && folderOk;
	}

	// ---------------------------------------------------------------- the tree

	// Used by the item template (x:Bind functions).
	public static string GlyphFor(bool isFolder) => isFolder ? "" : "";

	public static string SizeOf(long bytes) => ByteFormat.Size(bytes);

	private void BuildTree() {
		AddNodes(FileTree.RootNodes, _tree.Roots);
		if (_tree.Roots is [{ IsFolder: true }] && FileTree.RootNodes.Count == 1) {
			FileTree.RootNodes[0].IsExpanded = true; // a torrent that is just one folder: show what is in it
		}
		if (_tree.TotalCount == 1) {
			// One file: no folders to open and nothing to pick, so drop the tree chrome.
			Toolbar.Visibility = ColumnHeader.Visibility = Visibility.Collapsed;
			FileTree.Height = 48;
		}
		else {
			// Use the room the window has instead of a fixed small list.
			var window = (App.Window.Content as FrameworkElement)?.XamlRoot?.Size.Height ?? 800;
			FileTree.Height = Math.Clamp(window - 520, 220, 520);
		}
	}

	private static void AddNodes(IList<TreeViewNode> into, IEnumerable<FileTreeNode> nodes) {
		foreach (var node in nodes) {
			var item = new TreeViewNode { Content = node };
			AddNodes(item.Children, node.Children);
			into.Add(item);
		}
	}

	private static void SetExpanded(IEnumerable<TreeViewNode> nodes, bool expanded) {
		foreach (var node in nodes) {
			if (node.Children.Count > 0) {
				node.IsExpanded = expanded;
				SetExpanded(node.Children, expanded);
			}
		}
	}

	// The box has three states, but a click only ever means "all" or "none": a partly chosen folder becomes all.
	private void Node_Click(object sender, RoutedEventArgs e) {
		if (((FrameworkElement)sender).Tag is FileTreeNode node) {
			node.IsChecked = node.IsChecked != true;
			Refresh();
		}
	}

	private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetExpanded(FileTree.RootNodes, true);

	private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetExpanded(FileTree.RootNodes, false);

	private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

	private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

	private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

	private void SetAll(bool value) {
		_tree.SetAll(value);
		Refresh();
	}

	private void Seed_Changed(object sender, SelectionChangedEventArgs e) {
		// SelectedIndex="0" in the XAML raises this while the page is still loading, before RatioBox (declared below SeedBox) exists.
		if (RatioBox is null) {
			return;
		}
		RatioBox.Visibility = SeedBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
	}

	private async void Browse_Click(object sender, RoutedEventArgs e) {
		var picker = new FolderPicker();
		picker.FileTypeFilter.Add("*");
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
		if (await picker.PickSingleFolderAsync() is { } folder) {
			FolderBox.Text = folder.Path;
		}
	}
}
