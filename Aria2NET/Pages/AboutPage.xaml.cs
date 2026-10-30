using Aria2NET.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class AboutPage : Page {
	public AboutViewModel ViewModel { get; } = App.Services.GetRequiredService<AboutViewModel>();

	public AboutPage() {
		InitializeComponent();
	}
}
