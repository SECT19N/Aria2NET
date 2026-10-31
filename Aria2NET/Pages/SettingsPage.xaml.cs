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
	}

	private void ConfirmDelete_Click(object sender, RoutedEventArgs e) => ViewModel.ConfirmDeleteFiles = ConfirmDeleteBox.IsChecked == true;

	private async void OpenData_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(ViewModel.DataFolder);

	private async void OpenLogs_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchFolderPathAsync(ViewModel.LogsFolder);
}
