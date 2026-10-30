using Aria2NET.Core.Downloads;
using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Aria2NET.Pages;

public sealed partial class DownloadsPage : Page {
	private bool _dialogOpen;

	public DownloadsViewModel ViewModel { get; } = App.Services.GetRequiredService<DownloadsViewModel>();

	public DownloadsPage() {
		InitializeComponent();
	}

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
		if (_dialogOpen) {
			return;
		}
		_dialogOpen = true;
		try {
			var dialog = new AddDownloadDialog(ViewModel.InitialFolder(), prefill) { XamlRoot = XamlRoot };
			if (await dialog.ShowAsync() != ContentDialogResult.Primary) {
				return;
			}
			var error = await ViewModel.AddAsync(dialog.Urls, dialog.Folder, dialog.FileName);
			if (error is not null) {
				await new ContentDialog {
					Title = "Some downloads could not be added",
					Content = error,
					CloseButtonText = "OK",
					XamlRoot = XamlRoot,
				}.ShowAsync();
			}
		}
		finally {
			_dialogOpen = false;
		}
	}
}
