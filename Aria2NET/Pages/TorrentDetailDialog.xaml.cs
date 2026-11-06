using Aria2NET.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class TorrentDetailDialog : ContentDialog {
	public TorrentDetailViewModel ViewModel { get; }

	public TorrentDetailDialog(TorrentDetailViewModel viewModel) {
		ViewModel = viewModel;
		InitializeComponent();
		Opened += async (_, _) => await ViewModel.StartAsync();
		Closed += (_, _) => ViewModel.Stop();
	}
}
