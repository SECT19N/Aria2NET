using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class RenameDialog : ContentDialog {
	public string NewName => NameBox.Text.Trim();

	public RenameDialog(string currentName) {
		InitializeComponent();
		NameBox.Text = currentName;
		NameBox.SelectAll();
		Validate();
	}

	private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

	private void Validate() {
		var name = NewName;
		if (name.Length == 0) {
			ErrorText.Text = Loc.S("Rename_Empty", "Enter a file name.");
		}
		else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
			ErrorText.Text = Loc.S("Rename_Invalid", "A file name cannot contain \\ / : * ? \" < > |");
		}
		else {
			ErrorText.Text = "";
		}
		IsPrimaryButtonEnabled = ErrorText.Text.Length == 0;
	}
}
