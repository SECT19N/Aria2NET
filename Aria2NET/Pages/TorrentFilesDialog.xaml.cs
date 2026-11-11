using System.Collections.ObjectModel;
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
	private List<TorrentTreeItem> _roots = [];
	private bool _ready;

	/// <summary>The rows showing right now, in order.</summary>
	public ObservableCollection<TorrentTreeItem> Rows { get; } = [];

	public TorrentFilesDialog(TorrentPrompt prompt) {
		_tree = TorrentFileTree.Build(prompt.Files);
		InitializeComponent();

		NameText.Text = prompt.Name;
		ToolTipService.SetToolTip(NameText, prompt.Name);
		TotalText.Text = _tree.TotalCount == 1 ? Loc.F("Files_TotalOne", "1 file · {0}", ByteFormat.Size(_tree.TotalBytes)) : Loc.F("Files_TotalMany", "{0} files · {1}", _tree.TotalCount, ByteFormat.Size(_tree.TotalBytes));
		if (prompt.Note is { Length: > 0 } note) {
			NoteBar.Message = note;
			NoteBar.IsOpen = true;
		}
		BuildTree();
		FolderBox.Text = prompt.Folder;
		FolderBox.IsEnabled = BrowseButton.IsEnabled = prompt.FolderEditable;
		if (!prompt.FolderEditable) {
			ToolTipService.SetToolTip(FolderBox, Loc.S("Files_FolderFixed", "The folder was fixed when the magnet link was added."));
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
			1 => AllocationChoice.Full,
			_ => AllocationChoice.Default,
		});

	private void Refresh() {
		if (!_ready) {
			return;
		}
		var count = _tree.SelectedCount;
		var bytes = _tree.SelectedBytes;
		SummaryText.Text = Loc.F("Files_Selected", "{0} of {1} selected · {2}", count, _tree.TotalCount, ByteFormat.Size(bytes));

		var folder = FolderBox.Text.Trim();
		var folderOk = folder.Length > 0 && Path.IsPathFullyQualified(folder);
		var free = folderOk ? DiskSpace.FreeBytes(folder) : null;
		if (free is { } f) {
			var fits = bytes <= f;
			SpaceText.Text = fits ? Loc.F("Files_Free", "{0} free in this folder.", ByteFormat.Size(f)) : Loc.F("Files_NoSpace", "Not enough space: this needs {0} but only {1} is free.", ByteFormat.Size(bytes), ByteFormat.Size(f));
			SpaceText.Foreground = fits ? (Brush) Application.Current.Resources["TextFillColorSecondaryBrush"] : (Brush) Application.Current.Resources["SystemFillColorCriticalBrush"];
		} else {
			SpaceText.Text = "";
		}
		IsPrimaryButtonEnabled = count > 0 && folderOk;
	}

	// ---------------------------------------------------------------- the tree

	private void BuildTree() {
		_roots = _tree.Roots.Select(r => new TorrentTreeItem(r, 0, Refresh, OnExpandedChanged)).ToList();
		if (_roots is [{ Children.Count: > 0 } only]) {
			only.SetExpanded(true); // a torrent that is just one folder: show what is in it
		}
		RebuildRows();
		if (_tree.TotalCount == 1) {
			// One file: no folders to open and nothing to pick, so drop the tree chrome.
			Toolbar.Visibility = ColumnHeader.Visibility = Visibility.Collapsed;
			FileList.Height = 48;
		} else {
			// Use the room the window has instead of a fixed small list.
			var window = (App.Window.Content as FrameworkElement)?.XamlRoot?.Size.Height ?? 800;
			FileList.Height = Math.Clamp(window - 520, 220, 520);
		}
	}

	/// <summary>The list shows only what is visible: every folder row, and the rows under the folders that are open.</summary>
	private void RebuildRows() {
		Rows.Clear();
		foreach (var root in _roots) {
			Rows.Add(root);
			foreach (var below in root.VisibleDescendants()) {
				Rows.Add(below);
			}
		}
	}

	// Opening or closing one folder changes only the rows under it, so the list keeps its scroll position.
	private void OnExpandedChanged(TorrentTreeItem item) {
		var at = Rows.IndexOf(item);
		if (at < 0) {
			return;
		}
		if (item.IsExpanded) {
			var insertAt = at + 1;
			foreach (var below in item.VisibleDescendants()) {
				Rows.Insert(insertAt++, below);
			}
		} else {
			while (at + 1 < Rows.Count && Rows[at + 1].Depth > item.Depth) {
				Rows.RemoveAt(at + 1);
			}
		}
	}

	private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(true);

	private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(false);

	private void SetAllExpanded(bool value) {
		foreach (var root in _roots) {
			root.SetExpandedAll(value);
		}
		RebuildRows();
	}

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
