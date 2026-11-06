using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;
using Aria2NET.Services;
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
	// Static: a magnet link can ask for its files after the user has moved to another page, and only one dialog may be open at a time.
	private static bool _dialogOpen;

	public DownloadsViewModel ViewModel { get; } = App.Services.GetRequiredService<DownloadsViewModel>();

	public DownloadsPage() {
		InitializeComponent();
	}

	private void Page_Loaded(object sender, RoutedEventArgs e) {
		FilterBar.SelectedItem = FilterBar.Items.FirstOrDefault(i => (string)i.Tag == ViewModel.Filter.ToString()) ?? FilterBar.Items[0];
		DownloadList.SelectedItems.Clear();
		ViewModel.SelectionRestoreNeeded += OnSelectionRestoreNeeded;
		ViewModel.PromptTorrent = PromptTorrentAsync;
		ViewModel.ExternalAddRequested += OnExternalAddRequested;
		_ = HandleStartupAsync();
		ViewModel.SetSelection([]);
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e) {
		ViewModel.SelectionRestoreNeeded -= OnSelectionRestoreNeeded;
		ViewModel.ExternalAddRequested -= OnExternalAddRequested;
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

	// ---------------------------------------------------------------- opened from outside

	private async void OnExternalAddRequested(object? sender, EventArgs e) => await ShowExternalAddAsync();

	private async Task HandleStartupAsync() {
		await ShowExternalAddAsync();
		await OfferAssociationAsync();
	}

	/// <summary>A .torrent file or magnet link was opened with the app: show the Add dialog with it filled in (the engine may still be starting).</summary>
	private async Task ShowExternalAddAsync() {
		if (ViewModel.TakePendingExternalText() is not { } text) {
			return;
		}
		for (var i = 0; i < 75 && !ViewModel.IsEngineUp; i++) {
			await Task.Delay(200);
		}
		// Another dialog (the first-run offer, a file picker) may be open; wait instead of losing the link.
		while (_dialogOpen) {
			await Task.Delay(200);
		}
		await ShowAddDialogAsync(text);
	}

	/// <summary>Once, after an upgrade or first start: offer to open torrent files and magnet links with the app.</summary>
	private async Task OfferAssociationAsync() {
		var association = App.Services.GetRequiredService<TorrentAssociation>();
		if (!association.ShouldOffer) {
			return;
		}
		association.MarkOffered();
		var dialog = new ContentDialog {
			Title = "Open torrents with Aria2NET?",
			Content = "Aria2NET can open .torrent files and magnet links when you click them. Windows will ask you to confirm Aria2NET as the default app. You can change this later in Settings.",
			PrimaryButtonText = "Set up",
			CloseButtonText = "Not now",
			DefaultButton = ContentDialogButton.Primary,
		};
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) {
			if (!association.Enable()) {
				await TorrentAssociation.OpenDefaultAppsAsync();
			}
		}
	}

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
			text = await LinksFromAsync(Clipboard.GetContent());
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
			return; // Clipboard busy or unavailable.
		}
		if (UrlParser.Parse(text).Valid.Count > 0) {
			await ShowAddDialogAsync(text);
		}
	}

	private void Page_DragOver(object sender, DragEventArgs e) {
		var view = e.DataView;
		if (ViewModel.IsEngineUp && (view.Contains(StandardDataFormats.StorageItems) || view.Contains(StandardDataFormats.Text))) {
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.Caption = "Add to Aria2NET";
		}
	}

	private async void Page_Drop(object sender, DragEventArgs e) {
		string text;
		var deferral = e.GetDeferral();
		try {
			text = await LinksFromAsync(e.DataView);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
			return;
		}
		finally {
			deferral.Complete(); // the drag source must not wait for the dialog below
		}
		if (ViewModel.IsEngineUp && UrlParser.Parse(text).Valid.Count > 0) {
			await ShowAddDialogAsync(text);
		}
	}

	/// <summary>Links in the text of a clipboard or drop, plus the paths of any .torrent/.metalink files in it, one per line.</summary>
	private static async Task<string> LinksFromAsync(DataPackageView view) {
		var lines = new List<string>();
		if (view.Contains(StandardDataFormats.StorageItems)) {
			foreach (var item in await view.GetStorageItemsAsync()) {
				if (item is Windows.Storage.IStorageFile file && UrlParser.Classify(file.Path) is LinkKind.TorrentFile or LinkKind.MetalinkFile) {
					lines.Add("\"" + file.Path + "\"");
				}
			}
		}
		if (view.Contains(StandardDataFormats.Text)) {
			lines.Add(await view.GetTextAsync());
		}
		return string.Join("\r\n", lines);
	}

	private static async Task<TorrentChoice> PromptTorrentAsync(TorrentPrompt prompt) {
		var dialog = new TorrentFilesDialog(prompt);
		var result = await ShowDialogWhenFreeAsync(dialog);
		return result == ContentDialogResult.Primary ? dialog.Choice : dialog.Choice with { Files = [] };
	}

	/// <summary>Like <see cref="ShowDialogAsync"/>, but waits for the open dialog to close instead of giving up.</summary>
	private static async Task<ContentDialogResult> ShowDialogWhenFreeAsync(ContentDialog dialog) {
		while (_dialogOpen) {
			await Task.Delay(200);
		}
		_dialogOpen = true;
		try {
			dialog.XamlRoot = ((FrameworkElement)App.Window.Content).XamlRoot;
			return await dialog.ShowAsync();
		}
		finally {
			_dialogOpen = false;
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

	// Double-click opens the file; a multi-file torrent has no single file, so it opens its details instead.
	private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) {
		if (ViewModel.CanOpenFile) {
			ViewModel.OpenFile();
		}
		else if (ViewModel.CanShowTorrentDetails) {
			TorrentDetails_Click(sender, new RoutedEventArgs());
		}
	}

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

	private async void TorrentDetails_Click(object sender, RoutedEventArgs e) {
		if (ViewModel.CreateDetailViewModel() is { } detail) {
			await ShowDialogAsync(new TorrentDetailDialog(detail));
		}
	}

	private async void Recheck_Click(object sender, RoutedEventArgs e) => await ViewModel.RecheckSelectedAsync();

	private async void Seeding_Click(object sender, RoutedEventArgs e) {
		var dialog = new SeedingDialog();
		if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) {
			await ViewModel.SetSeedingSelectedAsync(dialog.Choice);
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
