using Aria2NET.Services;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Aria2NET.Pages;

public sealed partial class HistoryPage : Page {
	public HistoryViewModel ViewModel { get; } = App.Services.GetRequiredService<HistoryViewModel>();

	public HistoryPage() {
		InitializeComponent();
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();

	private void Find_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
		args.Handled = true;
		SearchBox.Focus(FocusState.Keyboard);
		SearchBox.SelectAll();
	}

	private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
		args.Handled = true;
		await ViewModel.ReloadAsync();
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();

	private void Direction_Click(object sender, RoutedEventArgs e) => ViewModel.Descending = !ViewModel.Descending;

	private void ClearFilters_Click(object sender, RoutedEventArgs e) => ViewModel.ClearFilters();

	private void ActionMessage_Closed(InfoBar sender, object args) => ViewModel.ClearMessage();

	// ---------------------------------------------------------------- list

	private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
		ViewModel.SetSelection(HistoryList.SelectedItems.OfType<HistoryRowViewModel>());

	private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args) {
		args.Handled = true;
		var menu = FlyoutBase.GetAttachedFlyout(HistoryList);
		if (args.TryGetPosition(HistoryList, out var point)) {
			// Right-clicking a row outside the selection selects just that row first.
			var host = HistoryList.TransformToVisual(null).TransformPoint(point);
			var hit = VisualTreeHelper.FindElementsInHostCoordinates(host, HistoryList)
				.OfType<FrameworkElement>()
				.Select(el => el.DataContext)
				.OfType<HistoryRowViewModel>()
				.FirstOrDefault();
			if (hit is not null && !HistoryList.SelectedItems.Contains(hit)) {
				HistoryList.SelectedItems.Clear();
				HistoryList.SelectedItems.Add(hit);
			}
			menu.ShowAt(HistoryList, new FlyoutShowOptions { Position = point });
		}
		else {
			menu.ShowAt(HistoryList);
		}
	}

	private async void List_KeyDown(object sender, KeyRoutedEventArgs e) {
		switch (e.Key) {
			case VirtualKey.Delete when ViewModel.CanDelete:
				e.Handled = true;
				await ConfirmAndDeleteAsync();
				break;
			case VirtualKey.Enter when ViewModel.CanOpenFile:
				e.Handled = true;
				ViewModel.OpenFile();
				break;
		}
	}

	private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) {
		if (ViewModel.CanOpenFile) {
			ViewModel.OpenFile();
		}
	}

	// ---------------------------------------------------------------- actions

	private async void Redownload_Click(object sender, RoutedEventArgs e) => await StartAgainAsync(null);

	private async void DownloadTo_Click(object sender, RoutedEventArgs e) {
		var picker = new FolderPicker();
		picker.FileTypeFilter.Add("*");
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
		if (await picker.PickSingleFolderAsync() is { } folder) {
			await StartAgainAsync(folder.Path);
		}
	}

	private async Task StartAgainAsync(string? folder) {
		var started = await ViewModel.RedownloadAsync(folder);
		if (started > 0) {
			App.Services.GetRequiredService<NoticeCenter>().Post(new Notice(
				started == 1 ? "Download started" : $"{started} downloads started",
				"You can follow it on the Downloads page.", InfoBarSeverity.Success));
		}
	}

	private void OpenFile_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFile();

	private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFolder();

	private void CopyUrl_Click(object sender, RoutedEventArgs e) => ViewModel.CopyUrl();

	private async void Delete_Click(object sender, RoutedEventArgs e) => await ConfirmAndDeleteAsync();

	private async Task ConfirmAndDeleteAsync() {
		var count = ViewModel.SelectedCount;
		if (count == 0) {
			return;
		}
		if (await App.Window.ConfirmAsync("Remove from history?",
				$"{count} {(count == 1 ? "entry" : "entries")} will be forgotten. Downloaded files are not deleted.", "Remove")) {
			await ViewModel.DeleteSelectedAsync();
		}
	}

	private async void ClearHistory_Click(object sender, RoutedEventArgs e) {
		if (await App.Window.ConfirmAsync("Clear all history?",
				"Every finished, failed and removed entry is forgotten. Downloads still in progress stay. Downloaded files are not deleted.", "Clear history")) {
			await ViewModel.ClearAllAsync();
		}
	}

	private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ExportAsync(json: false);

	private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAsync(json: true);

	private async Task ExportAsync(bool json) {
		var picker = new FileSavePicker { SuggestedFileName = "aria2net-history", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
		picker.FileTypeChoices.Add(json ? "JSON" : "CSV (opens in Excel)", [json ? ".json" : ".csv"]);
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
		if (await picker.PickSaveFileAsync() is { } file) {
			await ViewModel.ExportAsync(file.Path, json);
		}
	}
}
