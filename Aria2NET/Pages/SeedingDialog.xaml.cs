using Aria2NET.Core.Torrents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class SeedingDialog : ContentDialog {
	public SeedingDialog() {
		InitializeComponent();
	}

	public SeedingOverride Choice => ModeBox.SelectedIndex switch {
		1 => new SeedingOverride(SeedMode.Forever),
		2 => new SeedingOverride(SeedMode.Never),
		_ => new SeedingOverride(SeedMode.Ratio, double.IsNaN(RatioBox.Value) ? 1.0 : RatioBox.Value),
	};

	private void Mode_Changed(object sender, SelectionChangedEventArgs e) =>
		RatioBox.Visibility = ModeBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
}
