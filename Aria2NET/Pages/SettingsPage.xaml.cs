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
}
