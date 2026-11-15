using System.ComponentModel;
using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

	private void OnRowChanged(object? sender, PropertyChangedEventArgs e) {
		if (e.PropertyName == nameof(DownloadRowViewModel.StatusTip)) {
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

	private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

	private void DetailList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) => RowStripes.Apply(args);
}
