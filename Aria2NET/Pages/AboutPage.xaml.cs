using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class AboutPage : Page {
	public AboutViewModel ViewModel { get; } = App.Services.GetRequiredService<AboutViewModel>();

	public AboutPage() {
		InitializeComponent();
	}

	private async void License_Click(object sender, RoutedEventArgs e) => await ShowFileAsync(Loc.S("About_LicenseTitle", "License"), "LICENSE.txt");

	private async void Notices_Click(object sender, RoutedEventArgs e) => await ShowFileAsync(Loc.S("About_NoticesTitle", "Third-party notices"), "THIRD-PARTY-NOTICES.md");

	/// <summary>Shows a text file that ships next to the exe in a scrolling dialog.</summary>
	private async Task ShowFileAsync(string title, string fileName) {
		var text = AboutViewModel.ReadShippedText(fileName);
		var dialog = new ContentDialog {
			Title = title,
			Content = new ScrollViewer {
				MaxHeight = 420,
				Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
			},
			CloseButtonText = Loc.S("Common_Close", "Close"),
			XamlRoot = XamlRoot,
		};
		try {
			await dialog.ShowAsync();
		} catch (InvalidOperationException) {
			// Another dialog is already open.
		}
	}
}
