using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class SpeedLimitDialog : ContentDialog {
	/// <summary>Chosen limit in bytes per second; 0 = unlimited.</summary>
	public long BytesPerSecond => double.IsNaN(LimitBox.Value) ? 0 : (long)(LimitBox.Value * (UnitBox.SelectedIndex == 1 ? 1024 * 1024 : 1024));

	public SpeedLimitDialog(long currentBytesPerSecond) {
		InitializeComponent();
		if (currentBytesPerSecond >= 1024 * 1024 && currentBytesPerSecond % (1024 * 1024) == 0) {
			UnitBox.SelectedIndex = 1;
			LimitBox.Value = currentBytesPerSecond / (1024.0 * 1024);
		}
		else {
			LimitBox.Value = Math.Round(currentBytesPerSecond / 1024.0);
		}
	}

	private void LimitBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) {
		// Clearing the box leaves NaN; treat it as "not valid yet".
		IsPrimaryButtonEnabled = !double.IsNaN(sender.Value) && sender.Value >= 0;
	}

}
