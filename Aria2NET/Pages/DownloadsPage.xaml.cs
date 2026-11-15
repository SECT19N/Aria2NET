using Aria2NET.Core.Downloads;
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
		FilterBar.SelectedItem = FilterBar.Items.FirstOrDefault(i => (string) i.Tag == ViewModel.Filter.ToString()) ?? FilterBar.Items[0];
		DownloadList.SelectedItems.Clear();
		ViewModel.SelectionRestoreNeeded += OnSelectionRestoreNeeded;
		ViewModel.PromptTorrent = PromptTorrentAsync;
		ViewModel.PromptConflict = PromptConflictAsync;
		ViewModel.ExternalAddRequested += OnExternalAddRequested;
		ViewModel.Rows.CollectionChanged += Rows_Changed;
		ViewModel.PropertyChanged += OnViewModelChanged;
		DetailPane.Height = ViewModel.DetailsHeight;
		_ = HandleStartupAsync();
		ViewModel.SetSelection([]);
		if (_pendingReveal is { } gid) {
			_pendingReveal = null;
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => RevealDownload(gid));
		}
	}

	private string? _pendingReveal;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) {
		base.OnNavigatedTo(e);
		_pendingReveal = (e.Parameter as DownloadTarget)?.Gid;
	}

	/// <summary>Selects a download found by the title-bar search and scrolls to it, switching to the All filter when the current one hides it.</summary>
	public void RevealDownload(string gid) {
		if (ViewModel.Rows.All(r => r.Gid != gid)) {
			ViewModel.SetFilter(StatusFilter.All);
			FilterBar.SelectedItem = FilterBar.Items[0];
		}
		if (ViewModel.Rows.FirstOrDefault(r => r.Gid == gid) is { } row) {
			DownloadList.SelectedItem = row;
			DownloadList.ScrollIntoView(row);
			DownloadList.Focus(FocusState.Programmatic);
		}
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e) {
		ViewModel.SelectionRestoreNeeded -= OnSelectionRestoreNeeded;
		ViewModel.ExternalAddRequested -= OnExternalAddRequested;
		ViewModel.Rows.CollectionChanged -= Rows_Changed;
		ViewModel.PropertyChanged -= OnViewModelChanged;
		DetailPane.Show(null, null);
		ViewModel.FinishRestore([]);
	}

	// ---------------------------------------------------------------- details pane

	private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
		if (e.PropertyName is nameof(DownloadsViewModel.DetailRow) or nameof(DownloadsViewModel.ShowDetails)) {
			UpdateDetailPane();
		}
	}

	/// <summary>Shows the pane for the one selected download; hides it when none or several are selected, or when the user closed it.</summary>
	private void UpdateDetailPane() {
		var row = ViewModel.ShowDetails ? ViewModel.DetailRow : null;
		if (!ReferenceEquals(row, DetailPane.Row)) {
			DetailPane.Show(row, row is null ? null : ViewModel.CreateDetailViewModel(row));
		}
		var visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
		DetailPane.Visibility = visibility;
		PaneSizer.Visibility = visibility;
	}

	private void ShowDetails_Click(object sender, RoutedEventArgs e) => ViewModel.SetShowDetails(true);

	private void DetailPane_CloseRequested(object? sender, EventArgs e) => ViewModel.SetShowDetails(false);

	// The list always keeps room for a few rows, however small the window gets.
	private void ListArea_SizeChanged(object sender, SizeChangedEventArgs e) {
		DetailPane.MaxHeight = Math.Max(120, ListArea.ActualHeight - 150);
		UpdateNarrow();
	}

	private double _paneStartHeight;

	private void PaneSizer_DragStarted(object? sender, EventArgs e) => _paneStartHeight = DetailPane.ActualHeight;

	private void PaneSizer_Dragged(object? sender, double delta) => DetailPane.Height = Math.Clamp(_paneStartHeight - delta, 120, DetailPane.MaxHeight);

	private void PaneSizer_DragCompleted(object? sender, EventArgs e) => ViewModel.SaveDetailsHeight(DetailPane.Height);

	private void Rows_Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RowStripes.RefreshLater(DownloadList);

	// ---------------------------------------------------------------- row buttons, hover and narrow windows

	/// <summary>Whether the pointer is over a row; kept on the row's container because containers are recycled.</summary>
	private sealed class Hover {
		public bool Over;
	}

	private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) {
		RowStripes.Apply(args);
		if (args.ItemContainer is not ListViewItem item) {
			return;
		}
		if (item.Tag is not Hover) {
			item.Tag = new Hover();
			item.PointerEntered += (_, _) => SetOver(item, true);
			item.PointerExited += (_, _) => SetOver(item, false);
			item.GotFocus += (_, _) => ShowActions(item);
			item.LostFocus += (_, _) => ShowActions(item);
		}
		((Hover) item.Tag).Over = false; // a recycled container may still say true
		if (args.InRecycleQueue) {
			return;
		}
		ShowActions(item);
		if (item.ContentTemplateRoot is Grid row) {
			ApplyColumns(row);
		}
	}

	private static void SetOver(ListViewItem item, bool over) {
		if (item.Tag is Hover hover) {
			hover.Over = over;
		}
		ShowActions(item);
	}

	/// <summary>The row's buttons show under the pointer, on the selected rows and on the row with the keyboard focus; elsewhere they would only be noise.</summary>
	private static void ShowActions(ListViewItem item) {
		var visible = item.IsSelected || item.Tag is Hover { Over: true } || item.FocusState != FocusState.Unfocused;
		if (item.ContentTemplateRoot is FrameworkElement root && root.FindName("RowActions") is UIElement actions) {
			actions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	private static DownloadRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as DownloadRowViewModel;

	/// <summary>A button on a row acts on that row, so make it the selection first.</summary>
	private void SelectOnly(DownloadRowViewModel row) {
		if (DownloadList.SelectedItems.Count != 1 || !ReferenceEquals(DownloadList.SelectedItem, row)) {
			DownloadList.SelectedItems.Clear();
			DownloadList.SelectedItems.Add(row);
		}
		ViewModel.SetSelection(new[] { row });
	}

	private async void RowPrimary_Click(object sender, RoutedEventArgs e) {
		if (RowOf(sender) is not { } row) {
			return;
		}
		SelectOnly(row);
		switch (row.Item.Status) {
			case DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Seeding:
				await ViewModel.PauseSelectedAsync();
				break;
			case DownloadStatus.Paused:
				await ViewModel.ResumeSelectedAsync();
				break;
			default:
				await ViewModel.RetrySelectedAsync();
				break;
		}
	}

	private void RowFolder_Click(object sender, RoutedEventArgs e) {
		if (RowOf(sender) is { } row) {
			SelectOnly(row);
			ViewModel.OpenFolder();
		}
	}

	private async void RowRemove_Click(object sender, RoutedEventArgs e) {
		if (RowOf(sender) is { } row) {
			SelectOnly(row);
			await RemoveAsync(deleteFiles: false); // deleting files stays a deliberate choice in the menu
		}
	}

	// Narrow window: the Speed and Time left columns go and their text moves to the second line under the name.
	private bool _narrow;

	private void UpdateNarrow() {
		var width = ListArea.ActualWidth;
		Toolbar.DefaultLabelPosition = width >= 900 ? CommandBarDefaultLabelPosition.Right : CommandBarDefaultLabelPosition.Collapsed;
		var narrow = width > 0 && width < 860;
		if (narrow == _narrow) {
			return;
		}
		_narrow = narrow;
		ApplyColumns(ColumnHeader);
		for (var i = 0; i < DownloadList.Items.Count; i++) {
			if (DownloadList.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid row }) {
				ApplyColumns(row);
			}
		}
	}

	private void ApplyColumns(Grid grid) {
		if (grid.ColumnDefinitions.Count != 6) {
			return;
		}
		grid.ColumnDefinitions[3].Width = new GridLength(_narrow ? 0 : 100);
		grid.ColumnDefinitions[4].Width = new GridLength(_narrow ? 0 : 80);
		foreach (var child in grid.Children.OfType<FrameworkElement>()) {
			if (Grid.GetColumn(child) is 3 or 4) {
				child.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
			}
		}
		if (grid.FindName("CompactInfo") is UIElement info) {
			info.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
		}
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
			Title = Loc.S("Downloads_AssocTitle", "Open torrents with Aria2NET?"),
			Content = Loc.S("Downloads_AssocBody", "Aria2NET can open .torrent files and magnet links when you click them. Windows will ask you to confirm Aria2NET as the default app. You can change this later in Settings."),
			PrimaryButtonText = Loc.S("Downloads_AssocSetUp", "Set up"),
			CloseButtonText = Loc.S("Downloads_AssocNotNow", "Not now"),
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
		} catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
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
			e.DragUIOverride.Caption = Loc.S("Downloads_DragCaption", "Add to Aria2NET");
		}
	}

	private async void Page_Drop(object sender, DragEventArgs e) {
		string text;
		var deferral = e.GetDeferral();
		try {
			text = await LinksFromAsync(e.DataView);
		} catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
			return;
		} finally {
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
			dialog.XamlRoot = ((FrameworkElement) App.Window.Content).XamlRoot;
			return await dialog.ShowAsync();
		} finally {
			_dialogOpen = false;
		}
	}

	private async Task ShowAddDialogAsync(string? prefill) {
		var dialog = new AddDownloadDialog(ViewModel.InitialFolder(), prefill);
		if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) {
			return;
		}
		var extra = dialog.Extra;
		var error = await ViewModel.AddAsync(dialog.Urls, dialog.Folder, dialog.FileName, extra.ToOptions().Count == 0 ? null : extra);
		if (error is not null) {
			await ShowDialogAsync(new ContentDialog { Title = Loc.S("Downloads_AddFailedTitle", "Some downloads could not be added"), Content = error, CloseButtonText = Loc.S("Common_OK", "OK") });
		}
	}

	/// <summary>"A file with this name already exists": keep both, replace it, or skip this download.</summary>
	private async Task<ConflictChoice> PromptConflictAsync(string existingPath) {
		var dialog = new ContentDialog {
			Title = Loc.S("Downloads_ConflictTitle", "File already exists"),
			Content = Loc.F("Downloads_ConflictBody", "{0}\n\nKeep both files (the new one gets a number), or replace the existing file?", existingPath),
			PrimaryButtonText = Loc.S("Downloads_KeepBoth", "Keep both"),
			SecondaryButtonText = Loc.S("Downloads_Replace", "Replace"),
			CloseButtonText = Loc.S("Downloads_Skip", "Skip"),
			DefaultButton = ContentDialogButton.Primary,
		};
		// Closing with Esc counts as Skip, so nothing is overwritten by accident.
		var result = await ShowDialogAsync(dialog);
		return result switch {
			ContentDialogResult.Primary => ConflictChoice.Rename,
			ContentDialogResult.Secondary => ConflictChoice.Overwrite,
			_ => ConflictChoice.Skip,
		};
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
		} finally {
			_dialogOpen = false;
		}
	}

	// ---------------------------------------------------------------- list events

	private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) {
		ViewModel.SetSelection(DownloadList.SelectedItems.OfType<DownloadRowViewModel>());
		foreach (var changed in e.AddedItems.Concat(e.RemovedItems)) {
			if (DownloadList.ContainerFromItem(changed) is ListViewItem container) {
				ShowActions(container);
			}
		}
	}

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
		} else {
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
		} else if (ViewModel.DetailRow is not null) {
			ViewModel.SetShowDetails(true);
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

	private void AltSpeed_Click(object sender, RoutedEventArgs e) => _ = ViewModel.SetAltSpeedAsync(AltSpeedButton.IsChecked == true);

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
			var again = new CheckBox { Content = Loc.S("Downloads_DontAsk", "Don't ask me again") };
			var body = new StackPanel { Spacing = 12 };
			body.Children.Add(new TextBlock {
				Text = (count == 1 ? Loc.S("Downloads_DeleteOne", "Remove this download and delete its file?") : Loc.F("Downloads_DeleteMany", "Remove these {0} downloads and delete their files?", count))
					+ "\nFinished files go to the Recycle Bin. Partly downloaded files are deleted permanently.",
				TextWrapping = TextWrapping.Wrap,
			});
			body.Children.Add(again);
			var dialog = new ContentDialog {
				Title = Loc.S("Downloads_DeleteTitle", "Delete files?"),
				Content = body,
				PrimaryButtonText = Loc.S("Common_Delete", "Delete"),
				CloseButtonText = Loc.S("Common_Cancel", "Cancel"),
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
