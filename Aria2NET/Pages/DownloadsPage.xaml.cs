using Aria2NET.Core.Downloads;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Aria2NET.Pages;

public sealed partial class DownloadsPage : Page {
	private bool _dialogOpen;

	public DownloadsViewModel ViewModel { get; } = App.Services.GetRequiredService<DownloadsViewModel>();

	public DownloadsPage() {
		InitializeComponent();
	}

	private void Page_Loaded(object sender, RoutedEventArgs e) {
		FilterBar.SelectedItem = FilterBar.Items.FirstOrDefault(i => (string)i.Tag == ViewModel.Filter.ToString()) ?? FilterBar.Items[0];
		DownloadList.SelectedItems.Clear();
		ViewModel.SelectionRestoreNeeded += OnSelectionRestoreNeeded;
		ViewModel.SetSelection([]);
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e) {
		ViewModel.SelectionRestoreNeeded -= OnSelectionRestoreNeeded;
		ViewModel.FinishRestore([]);
	}

	// Moving a row (e.g. a paused download dropping below the active ones) makes ListView forget its selection; put it back.
	private void OnSelectionRestoreNeeded(object? sender, IReadOnlyList<DownloadRowViewModel> rows) =>
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
			foreach (var row in rows) {
				if (ViewModel.Rows.Contains(row) && !DownloadList.SelectedItems.Contains(row)) {
					DownloadList.SelectedItems.Add(row);
				}
			}
			ViewModel.FinishRestore(DownloadList.SelectedItems.OfType<DownloadRowViewModel>());
		});

	// ---------------------------------------------------------------- adding

	private async void Add_Click(object sender, RoutedEventArgs e) => await ShowAddDialogAsync(null);

	private async void Paste_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) {
		// Let normal text boxes keep their own paste.
		if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox || !ViewModel.IsEngineUp) {
			return;
		}
		args.Handled = true;
		string text;
		try {
			var content = Clipboard.GetContent();
			if (!content.Contains(StandardDataFormats.Text)) {
				return;
			}
			text = await content.GetTextAsync();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
			return; // Clipboard busy or unavailable.
		}
		if (UrlParser.Parse(text).Valid.Count > 0) {
			await ShowAddDialogAsync(text);
		}
	}

	private async Task ShowAddDialogAsync(string? prefill) {
		var dialog = new AddDownloadDialog(ViewModel.InitialFolder(), prefill);
		if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) {
			return;
		}
		var error = await ViewModel.AddAsync(dialog.Urls, dialog.Folder, dialog.FileName);
		if (error is not null) {
			await ShowDialogAsync(new ContentDialog { Title = "Some downloads could not be added", Content = error, CloseButtonText = "OK" });
		}
	}

	/// <summary>Only one dialog can be open at a time in WinUI.</summary>
	private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog) {
		if (_dialogOpen) {
			return ContentDialogResult.None;
		}
		_dialogOpen = true;
		try {
			dialog.XamlRoot = XamlRoot;
			return await dialog.ShowAsync();
		}
		finally {
			_dialogOpen = false;
		}
	}

	// ---------------------------------------------------------------- list events

	private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
		ViewModel.SetSelection(DownloadList.SelectedItems.OfType<DownloadRowViewModel>());

	private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args) {
		args.Handled = true;
		var menu = FlyoutBase.GetAttachedFlyout(DownloadList);
		if (args.TryGetPosition(DownloadList, out var point)) {
			// Right-clicking a row outside the selection selects just that row first.
			var host = DownloadList.TransformToVisual(null).TransformPoint(point);
			var hit = VisualTreeHelper.FindElementsInHostCoordinates(host, DownloadList)
				.OfType<FrameworkElement>()
				.Select(el => el.DataContext)
				.OfType<DownloadRowViewModel>()
				.FirstOrDefault();
			if (hit is not null && !DownloadList.SelectedItems.Contains(hit)) {
				DownloadList.SelectedItems.Clear();
				DownloadList.SelectedItems.Add(hit);
			}
			menu.ShowAt(DownloadList, new FlyoutShowOptions { Position = point });
		}
		else {
			menu.ShowAt(DownloadList);
		}
	}

	private async void List_KeyDown(object sender, KeyRoutedEventArgs e) {
		switch (e.Key) {
			case VirtualKey.Delete:
				e.Handled = true;
				await RemoveAsync(deleteFiles: InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down));
				break;
			case VirtualKey.Space:
				e.Handled = true;
				await ViewModel.TogglePauseSelectedAsync();
				break;
			case VirtualKey.Enter:
				e.Handled = true;
				ViewModel.OpenFile();
				break;
		}
	}

	private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ViewModel.OpenFile();

	private void Filter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) {
		if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<StatusFilter>(tag, out var filter)) {
			ViewModel.SetFilter(filter);
		}
	}

	private void Header_Click(object sender, RoutedEventArgs e) {
		if (sender is Button { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var column)) {
			ViewModel.ToggleSort(column);
		}
	}

	private void ActionMessage_Closed(InfoBar sender, object args) => ViewModel.ClearMessage();

	// ---------------------------------------------------------------- simple actions

	private async void Pause_Click(object sender, RoutedEventArgs e) => await ViewModel.PauseSelectedAsync();

	private async void Resume_Click(object sender, RoutedEventArgs e) => await ViewModel.ResumeSelectedAsync();

	private async void Retry_Click(object sender, RoutedEventArgs e) => await ViewModel.RetrySelectedAsync();

	private async void PauseAll_Click(object sender, RoutedEventArgs e) => await ViewModel.PauseAllAsync();

	private async void ResumeAll_Click(object sender, RoutedEventArgs e) => await ViewModel.ResumeAllAsync();

	private async void ClearCompleted_Click(object sender, RoutedEventArgs e) => await ViewModel.ClearAsync(failed: false);

	private async void ClearFailed_Click(object sender, RoutedEventArgs e) => await ViewModel.ClearAsync(failed: true);

	private async void ForceStart_Click(object sender, RoutedEventArgs e) => await ViewModel.ForceStartSelectedAsync();

	private async void MoveTop_Click(object sender, RoutedEventArgs e) => await ViewModel.MoveSelectedAsync(QueueMove.Top);

	private async void MoveUp_Click(object sender, RoutedEventArgs e) => await ViewModel.MoveSelectedAsync(QueueMove.Up);

	private async void MoveDown_Click(object sender, RoutedEventArgs e) => await ViewModel.MoveSelectedAsync(QueueMove.Down);

	private async void MoveBottom_Click(object sender, RoutedEventArgs e) => await ViewModel.MoveSelectedAsync(QueueMove.Bottom);

	private void OpenFile_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFile();

	private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel.OpenFolder();

	private void CopyUrl_Click(object sender, RoutedEventArgs e) => ViewModel.CopyUrl();

	private void Undo_Click(object sender, RoutedEventArgs e) => ViewModel.UndoRemove();

	// ---------------------------------------------------------------- remove

	private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveAsync(deleteFiles: false);

	private async void RemoveDelete_Click(object sender, RoutedEventArgs e) => await RemoveAsync(deleteFiles: true);

	private async Task RemoveAsync(bool deleteFiles) {
		if (!ViewModel.CanRemove) {
			return;
		}
		if (deleteFiles && ViewModel.ConfirmDeleteFiles) {
			var count = ViewModel.SelectedCount;
			var again = new CheckBox { Content = "Don't ask me again" };
			var body = new StackPanel { Spacing = 12 };
			body.Children.Add(new TextBlock {
				Text = (count == 1 ? "Remove this download and delete its file?" : $"Remove these {count} downloads and delete their files?")
					+ "\nFinished files go to the Recycle Bin. Partly downloaded files are deleted permanently.",
				TextWrapping = TextWrapping.Wrap,
			});
			body.Children.Add(again);
			var dialog = new ContentDialog {
				Title = "Delete files?",
				Content = body,
				PrimaryButtonText = "Delete",
				CloseButtonText = "Cancel",
				DefaultButton = ContentDialogButton.Close,
			};
			if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) {
				return;
			}
			if (again.IsChecked == true) {
				ViewModel.DisableDeleteConfirmation();
			}
		}
		await ViewModel.RemoveSelectedAsync(deleteFiles);
	}

	// ---------------------------------------------------------------- dialogs

	private async void Rename_Click(object sender, RoutedEventArgs e) {
		if (ViewModel.SingleSelected() is not { } item) {
			return;
		}
		var dialog = new RenameDialog(item.Name);
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) {
			await ViewModel.RenameSelectedAsync(dialog.NewName);
		}
	}

	private async void SpeedLimit_Click(object sender, RoutedEventArgs e) {
		// Start from the limit already set on a single download.
		var current = ViewModel.SelectedCount == 1 && await ViewModel.GetDetailsAsync() is { } details ? details.SpeedLimitBytesPerSec : 0;
		var dialog = new SpeedLimitDialog(current);
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) {
			await ViewModel.SetSpeedLimitSelectedAsync(dialog.BytesPerSecond);
		}
	}

	private async void ChangeFolder_Click(object sender, RoutedEventArgs e) {
		var picker = new FolderPicker();
		picker.FileTypeFilter.Add("*");
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
		var folder = await picker.PickSingleFolderAsync();
		if (folder is not null) {
			await ViewModel.SetFolderSelectedAsync(folder.Path);
		}
	}

	private async void Properties_Click(object sender, RoutedEventArgs e) {
		if (await ViewModel.GetDetailsAsync() is not { } details) {
			return;
		}
		var (added, finished) = ViewModel.Times(details.Item.Gid);
		await ShowDialogAsync(new PropertiesDialog(details, added, finished));
	}
}
