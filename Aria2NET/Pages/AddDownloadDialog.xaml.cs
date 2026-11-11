using Aria2NET.Core.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Aria2NET.Pages;

public sealed partial class AddDownloadDialog : ContentDialog {
	private UrlParseResult _parsed = new([], []);

	public IReadOnlyList<string> Urls => _parsed.Valid;
	public string Folder => FolderBox.Text.Trim();
	public string? FileName => string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim();

	private readonly HeaderPresets _presets = App.Services.GetRequiredService<HeaderPresets>();
	private bool _loading;

	/// <summary>The "More options" values. Empty fields are left out.</summary>
	public AddOptions Extra {
		get {
			var user = UserBox.Text.Trim();
			var password = PasswordBox.Password;
			return new AddOptions(
				Lines(HeadersBox.Text),
				NullIfBlank(RefererBox.Text),
				NullIfBlank(CookieBox.Text),
				NullIfBlank(user), password.Length == 0 ? null : password,
				NullIfBlank(user), password.Length == 0 ? null : password,
				NullIfBlank(ChecksumBox.Text),
				Limit(DownLimitBox),
				Limit(UpLimitBox));
		}
	}

	private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

	private static List<string> Lines(string text) =>
		text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

	private static int Limit(NumberBox box) => double.IsNaN(box.Value) ? 0 : (int)Math.Max(0, Math.Round(box.Value));

	public AddDownloadDialog(string initialFolder, string? prefilledText) {
		InitializeComponent();
		FolderBox.Text = initialFolder;
		UrlsBox.Text = prefilledText ?? "";
		ReloadPresets(null);
		Validate();
	}

	private void ReloadPresets(string? select) {
		_loading = true;
		try {
			PresetBox.Items.Clear();
			foreach (var preset in _presets.List()) {
				PresetBox.Items.Add(preset.Name);
			}
			PresetBox.SelectedItem = select;
			DeletePresetButton.IsEnabled = select is not null;
		}
		finally {
			_loading = false;
		}
	}

	private void Preset_Changed(object sender, SelectionChangedEventArgs e) {
		if (_loading || PresetBox.SelectedItem is not string name) {
			return;
		}
		DeletePresetButton.IsEnabled = true;
		if (_presets.List().FirstOrDefault(p => p.Name == name) is { } preset) {
			HeadersBox.Text = string.Join("\r", preset.Headers);
			RefererBox.Text = preset.Referer ?? "";
			CookieBox.Text = HeaderPresets.Cookie(preset);
			MoreOptions.IsExpanded = true;
		}
	}

	private void SavePreset_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) {
		PresetNameBox.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
		PresetNameBox.Text = PresetBox.SelectedItem as string ?? "";
		PresetNameBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
	}

	private void PresetName_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) {
		if (e.Key == Windows.System.VirtualKey.Escape) {
			PresetNameBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
			e.Handled = true;
		}
		else if (e.Key == Windows.System.VirtualKey.Enter) {
			e.Handled = true; // keep Enter from pressing the dialog's Download button
			var name = PresetNameBox.Text.Trim();
			var error = _presets.Save(name, Lines(HeadersBox.Text), NullIfBlank(RefererBox.Text), NullIfBlank(CookieBox.Text));
			OptionsError.Text = error ?? "";
			if (error is null) {
				PresetNameBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
				ReloadPresets(name);
			}
		}
	}

	private void DeletePreset_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) {
		if (PresetBox.SelectedItem is string name) {
			_presets.Delete(name);
			ReloadPresets(null);
		}
	}

	private void Option_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Validate();

	private void Limit_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => Validate();

	private void UrlsBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

	private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

	private void Validate() {
		_parsed = UrlParser.Parse(UrlsBox.Text);
		var valid = _parsed.Valid.Count;
		var invalid = _parsed.Invalid.Count;
		ValidationText.Text = (valid, invalid) switch {
			(0, 0) => "",
			(0, _) => Loc.F("Add_NothingUsable", "Nothing usable. Use http, https, ftp, sftp, a magnet link or a .torrent/.metalink file. Not recognized: {0}", invalid),
			(_, 0) => valid == 1 ? Loc.S("Add_OneLinkReady", "1 link ready") : Loc.F("Add_LinksReady", "{0} links ready", valid),
			_ => valid == 1 ? Loc.F("Add_OneLinkReadySkip", "1 link ready; {0} not recognized and will be skipped", invalid) : Loc.F("Add_LinksReadySkip", "{0} links ready; {1} not recognized and will be skipped", valid, invalid),
		};
		FileNameBox.IsEnabled = valid == 1 && UrlParser.Classify(_parsed.Valid[0]) == LinkKind.Uri;
		if (!FileNameBox.IsEnabled) {
			FileNameBox.Text = "";
		}
		var problem = Extra.Validate();
		if (!_loading && PresetNameBox.Visibility != Microsoft.UI.Xaml.Visibility.Visible) {
			OptionsError.Text = problem ?? "";
		}
		if (problem is not null) {
			MoreOptions.IsExpanded = true;
		}
		IsPrimaryButtonEnabled = valid > 0 && Folder.Length > 0 && Path.IsPathFullyQualified(Folder) && problem is null;
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
