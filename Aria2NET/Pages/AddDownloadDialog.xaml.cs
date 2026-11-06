using Aria2NET.Core.Downloads;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Aria2NET.Pages;

public sealed partial class AddDownloadDialog : ContentDialog {
	private UrlParseResult _parsed = new([], []);

	public IReadOnlyList<string> Urls => _parsed.Valid;
	public string Folder => FolderBox.Text.Trim();
	public string? FileName => string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim();

	public AddDownloadDialog(string initialFolder, string? prefilledText) {
		InitializeComponent();
		FolderBox.Text = initialFolder;
		UrlsBox.Text = prefilledText ?? "";
		Validate();
	}

	private void UrlsBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

	private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

	private void Validate() {
		_parsed = UrlParser.Parse(UrlsBox.Text);
		var valid = _parsed.Valid.Count;
		var invalid = _parsed.Invalid.Count;
		ValidationText.Text = (valid, invalid) switch {
			(0, 0) => "",
			(0, _) => $"Nothing usable. Use http, https, ftp, sftp, a magnet link or a .torrent/.metalink file. Not recognized: {invalid}",
			(_, 0) => $"{valid} link{(valid == 1 ? "" : "s")} ready",
			_ => $"{valid} link{(valid == 1 ? "" : "s")} ready; {invalid} not recognized and will be skipped",
		};
		FileNameBox.IsEnabled = valid == 1 && UrlParser.Classify(_parsed.Valid[0]) == LinkKind.Uri;
		if (!FileNameBox.IsEnabled) {
			FileNameBox.Text = "";
		}
		IsPrimaryButtonEnabled = valid > 0 && Folder.Length > 0 && Path.IsPathFullyQualified(Folder);
	}

	private async void Browse_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) {
		var picker = new FolderPicker();
		picker.FileTypeFilter.Add("*");
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
		var folder = await picker.PickSingleFolderAsync();
		if (folder is not null) {
			FolderBox.Text = folder.Path;
		}
	}

	private async void BrowseFiles_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) {
		var picker = new FileOpenPicker();
		foreach (var extension in new[] { ".torrent", ".metalink", ".meta4" }) {
			picker.FileTypeFilter.Add(extension);
		}
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
		var files = await picker.PickMultipleFilesAsync();
		if (files is null || files.Count == 0) {
			return;
		}
		var lines = files.Select(f => "\"" + f.Path + "\"");
		UrlsBox.Text = string.Join("\r\n", UrlsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Concat(lines));
	}
}
