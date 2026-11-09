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
	private readonly TorrentPrompt _prompt;
	private bool _ready;

	public ObservableCollection<TorrentFileChoice> Choices { get; }

	public TorrentFilesDialog(TorrentPrompt prompt) {
		_prompt = prompt;
		Choices = new(prompt.Files.Select(f => new TorrentFileChoice(f.Index, f.Path, f.Length)));
		InitializeComponent();

		NameText.Text = prompt.Name;
		if (prompt.Note is { Length: > 0 } note) {
			NoteText.Text = note;
			NoteText.Visibility = Visibility.Visible;
		}
		FolderBox.Text = prompt.Folder;
		FolderBox.IsEnabled = BrowseButton.IsEnabled = prompt.FolderEditable;
		if (!prompt.FolderEditable) {
			ToolTipService.SetToolTip(FolderBox, "The folder was fixed when the magnet link was added.");
		}
		_ready = true;
		Refresh();
	}

	/// <summary>What the user chose. No files means the dialog was cancelled.</summary>
	public TorrentChoice Choice => new(
		FolderBox.Text.Trim(),
		Choices.Where(c => c.IsSelected).Select(c => c.Index).ToList(),
		SeedBox.SelectedIndex switch {
			1 => new SeedingOverride(SeedMode.Forever),
			2 => new SeedingOverride(SeedMode.Ratio, double.IsNaN(RatioBox.Value) ? 1.0 : RatioBox.Value),
			3 => new SeedingOverride(SeedMode.Never),
			_ => SeedingOverride.Default,
		},
		AllocationBox.SelectedIndex switch {
			1 => AllocationChoice.Sparse,
			2 => AllocationChoice.Full,
			_ => AllocationChoice.Default,
		});

	private void Refresh() {
		if (!_ready) {
			return;
		}
		var chosen = Choices.Where(c => c.IsSelected).ToList();
		var bytes = chosen.Sum(c => c.Length);
		SummaryText.Text = $"{chosen.Count} of {Choices.Count} selected · {ByteFormat.Size(bytes)}";

		var folder = FolderBox.Text.Trim();
		var folderOk = folder.Length > 0 && Path.IsPathFullyQualified(folder);
		var free = folderOk ? DiskSpace.FreeBytes(folder) : null;
		if (free is { } f) {
			var fits = bytes <= f;
			SpaceText.Text = fits ? $"{ByteFormat.Size(f)} free in this folder." : $"Not enough space: this needs {ByteFormat.Size(bytes)} but only {ByteFormat.Size(f)} is free.";
			SpaceText.Foreground = fits ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
		}
		else {
			SpaceText.Text = "";
		}
		IsPrimaryButtonEnabled = chosen.Count > 0 && folderOk;
	}

	private void Choice_Changed(object sender, RoutedEventArgs e) => Refresh();

	private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

	private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

	private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

	private void SetAll(bool value) {
		foreach (var choice in Choices) {
			choice.IsSelected = value;
		}
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
