using System.Collections.Specialized;
using System.ComponentModel;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Shell;
using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Aria2NET.Controls;

/// <summary>The panel under the downloads list: everything about the one selected download, updated live.</summary>
public sealed partial class DownloadDetailPane : UserControl {
	// Static: the tab the user was reading stays open from one download to the next.
	private static string _tab = "General";

	/// <summary>The list row of the download shown (status, progress, time left).</summary>
	public DownloadRowViewModel? Row { get; private set; }

	/// <summary>What the list does not carry: files, peers, trackers, pieces.</summary>
	public DownloadDetailViewModel? Detail { get; private set; }

	/// <summary>The user pressed the pane's close button.</summary>
	public event EventHandler? CloseRequested;

	public DownloadDetailPane() {
		InitializeComponent();
		Unloaded += (_, _) => Show(null, null);
	}

	/// <summary>Switches the pane to another download, or to none (which stops the refreshing).</summary>
	public void Show(DownloadRowViewModel? row, DownloadDetailViewModel? detail) {
		if (Detail is not null) {
			Detail.Stop();
			Detail.PropertyChanged -= OnDetailChanged;
			Detail.Files.CollectionChanged -= OnFilesChanged;
		}
		if (Row is not null) {
			Row.PropertyChanged -= OnRowChanged;
		}
		Row = row;
		Detail = detail;
		Bindings.Update();
		if (row is not null) {
			row.PropertyChanged += OnRowChanged;
		}
		if (detail is not null) {
			detail.PropertyChanged += OnDetailChanged;
			detail.Files.CollectionChanged += OnFilesChanged;
			_ = detail.StartAsync();
		}
		Refresh();
	}

	/// <summary>Opens one of the tabs by name ("Files", "Peers" …) when the download has it.</summary>
	public void SelectTab(string tab) {
		_tab = tab;
		Refresh();
	}

	private void OnDetailChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName is nameof(DownloadDetailViewModel.IsTorrent) or nameof(DownloadDetailViewModel.Problem) or nameof(DownloadDetailViewModel.Source)) {
			Refresh();
		}
	}

	private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

	private void OnRowChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName is nameof(DownloadRowViewModel.StatusTip) or nameof(DownloadRowViewModel.StatusText)) {
			Refresh();
		}
	}

	private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) {
		if (!_refreshing && sender.SelectedItem?.Tag is string tab) {
			_tab = tab;
			Refresh();
		}
	}

	private bool _refreshing;

	/// <summary>Shows what applies to this download: the torrent-only tabs and lines, the error line, and the selected tab's panel.</summary>
	private void Refresh() {
		if (Tabs is null || PiecesPanel is null) {
			return; // still loading the XAML
		}
		var torrent = Row?.Item is { IsTorrent: true, IsMetadata: false };
		var torrentOnly = torrent ? Visibility.Visible : Visibility.Collapsed;
		PeersTab.Visibility = torrentOnly;
		TrackersTab.Visibility = torrentOnly;
		PiecesTab.Visibility = torrentOnly;
		TorrentLines.Visibility = torrentOnly;
		UploadedLabel.Visibility = torrentOnly;
		UploadedValue.Visibility = torrentOnly;
		SourceLine.Visibility = string.IsNullOrEmpty(Detail?.Source) ? Visibility.Collapsed : Visibility.Visible;
		ErrorLine.Visibility = Row?.Item.Status == Aria2NET.Core.Downloads.DownloadStatus.Error && !string.IsNullOrEmpty(Row.StatusTip) ? Visibility.Visible : Visibility.Collapsed;
		ProblemText.Visibility = string.IsNullOrEmpty(Detail?.Problem) ? Visibility.Collapsed : Visibility.Visible;
		var editable = torrent && Row?.Item.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused;
		Detail?.SetEditable(editable);
		SelectAllButton.IsEnabled = editable;
		FilesTools.Visibility = Detail is { Files.Count: > 1 } ? Visibility.Visible : Visibility.Collapsed;

		// A plain download has no peers, trackers or pieces: show General instead, but remember the user's choice for the next torrent.
		var tab = torrent || _tab is "General" or "Files" ? _tab : "General";
		_refreshing = true;
		try {
			Tabs.SelectedItem = Tabs.Items.FirstOrDefault(i => (string) i.Tag == tab) ?? Tabs.Items[0];
		} finally {
			_refreshing = false;
		}
		GeneralPanel.Visibility = tab == "General" ? Visibility.Visible : Visibility.Collapsed;
		FilesPanel.Visibility = tab == "Files" ? Visibility.Visible : Visibility.Collapsed;
		PeersPanel.Visibility = tab == "Peers" ? Visibility.Visible : Visibility.Collapsed;
		TrackersPanel.Visibility = tab == "Trackers" ? Visibility.Visible : Visibility.Collapsed;
		PiecesPanel.Visibility = tab == "Pieces" ? Visibility.Visible : Visibility.Collapsed;
	}

	// ---------------------------------------------------------------- files

	private async void FileCheck_Click(object sender, RoutedEventArgs e) {
		if (Detail is not null && ((FrameworkElement) sender).DataContext is DetailFileRow row) {
			await Detail.ChooseAsync(row);
		}
	}

	private async void SelectAll_Click(object sender, RoutedEventArgs e) {
		if (Detail is not null) {
			await Detail.ChooseAllAsync();
		}
	}

	private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e) {
		if (Detail is not { } detail || (e.OriginalSource as FrameworkElement)?.DataContext is not DetailFileRow row) {
			return;
		}
		var exists = File.Exists(row.FullPath);
		var menu = new MenuFlyout();
		menu.Items.Add(Entry(Loc.S("Detail_OpenFile", "Open file"), exists, () => ShellLauncher.TryOpenFile(row.FullPath, out _)));
		menu.Items.Add(Entry(Loc.S("Detail_ShowInFolder", "Show in folder"), !string.IsNullOrEmpty(row.FullPath), () => {
			var folder = Path.GetDirectoryName(row.FullPath);
			if (!string.IsNullOrEmpty(folder)) {
				ShellLauncher.TryOpenFolder(folder, exists ? row.FullPath : null, out _);
			}
		}));
		menu.Items.Add(Entry(Loc.S("Detail_CopyPath", "Copy path"), !string.IsNullOrEmpty(row.FullPath), () => {
			var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
			package.SetText(row.FullPath);
			Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
		}));
		if (row.ShowChoice) {
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(Entry(row.Selected ? Loc.S("Detail_SkipFile", "Skip this file") : Loc.S("Detail_GetFile", "Download this file"), row.CanChoose, async () => {
				row.Selected = !row.Selected;
				await detail.ChooseAsync(row);
			}));
			menu.Items.Add(Entry(Loc.S("Detail_OnlyThisFile", "Download only this file"), row.CanChoose, () => detail.ChooseOnlyAsync(row)));
		}
		menu.ShowAt(FileList, e.GetPosition(FileList));
		e.Handled = true;
	}

	private static MenuFlyoutItem Entry(string text, bool enabled, Action click) {
		var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
		item.Click += (_, _) => click();
		return item;
	}

	private static MenuFlyoutItem Entry(string text, bool enabled, Func<Task> click) {
		var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
		item.Click += async (_, _) => await click();
		return item;
	}

	private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

	private void DetailList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) => RowStripes.Apply(args);
}
