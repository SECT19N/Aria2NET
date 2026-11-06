using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Torrents;
using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Aria2NET.Pages;

public sealed partial class SettingsPage : Page {
	public SettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<SettingsViewModel>();

	public SettingsPage() {
		InitializeComponent();
		ConfirmDeleteBox.IsChecked = ViewModel.ConfirmDeleteFiles;
		foreach (var choice in SettingsViewModel.RetentionChoices) {
			RetentionBox.Items.Add(choice.Label);
		}
		RetentionBox.SelectedIndex = ViewModel.RetentionIndex;
		RetentionBox.IsEnabled = ViewModel.HistoryAvailable;
		RefreshAssociation();
		TrackerStatus.Text = TrackerSummary();
	}

	private void Retention_Changed(object sender, SelectionChangedEventArgs e) {
		if (RetentionBox.SelectedIndex >= 0 && RetentionBox.SelectedIndex != ViewModel.RetentionIndex) {
			ViewModel.RetentionIndex = RetentionBox.SelectedIndex;
		}
	}

	private async void ClearHistory_Click(object sender, RoutedEventArgs e) {
		if (await App.Window.ConfirmAsync("Clear all history?", "Every finished, failed and removed entry is forgotten. Downloads still in progress stay. Downloaded files are not deleted.", "Clear history")) {
			await Task.Run(ViewModel.ClearHistory);
		}
	}

	private void ConfirmDelete_Click(object sender, RoutedEventArgs e) => ViewModel.ConfirmDeleteFiles = ConfirmDeleteBox.IsChecked == true;

	private async void OpenData_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(ViewModel.DataFolder);

	private async void OpenLogs_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(ViewModel.LogsFolder);

	// ---------------------------------------------------------------- torrents

	private static TorrentAssociation Association => App.Services.GetRequiredService<TorrentAssociation>();

	private void RefreshAssociation() {
		var association = Association;
		AssociationBox.IsEnabled = association.IsAvailable;
		var state = association.State;
		AssociationBox.IsChecked = state.Registered;
		DefaultAppsLink.Visibility = state.Registered && !(state.DefaultForTorrent && state.DefaultForMagnet) ? Visibility.Visible : Visibility.Collapsed;
		AssociationStatus.Text = !state.Registered
			? ""
			: state.DefaultForTorrent && state.DefaultForMagnet
				? "Aria2NET is the default app for .torrent files and magnet links."
				: "Aria2NET is offered to Windows. Windows lets only you choose the default app: pick Aria2NET for .torrent files and for the MAGNET link type.";
	}

	private async void Association_Click(object sender, RoutedEventArgs e) {
		if (AssociationBox.IsChecked == true) {
			if (!Association.Enable()) {
				RefreshAssociation();
				await TorrentAssociation.OpenDefaultAppsAsync();
			}
		}
		else {
			Association.Disable();
		}
		RefreshAssociation();
	}

	private async void DefaultApps_Click(object sender, RoutedEventArgs e) => await TorrentAssociation.OpenDefaultAppsAsync();

	private static string TrackerSummary() {
		var paths = App.Services.GetRequiredService<AppPaths>();
		var count = TrackerListService.ReadCache(paths).Count;
		return count == 0 ? "No list downloaded yet." : $"{count} trackers cached; refreshed automatically every week.";
	}

	private async void UpdateTrackers_Click(object sender, RoutedEventArgs e) {
		TrackerStatus.Text = "Downloading…";
		var list = await App.Services.GetRequiredService<TrackerListService>().RefreshAsync();
		if (list is not null && App.Services.GetRequiredService<IAria2Engine>().Rpc is { } rpc) {
			try {
				await rpc.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["bt-tracker"] = Aria2Config.TrackerOptionValue(list) });
			}
			catch (Exception ex) when (ex is Aria2RpcException or HttpRequestException) {
				// The new list is cached and used at the next engine start.
			}
		}
		TrackerStatus.Text = list is null ? "Could not download the list; keeping the old one." : TrackerSummary();
	}
}
