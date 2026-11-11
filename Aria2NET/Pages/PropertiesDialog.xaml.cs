using Aria2NET.Core.Downloads;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class PropertiesDialog : ContentDialog {
	public PropertiesDialog(DownloadDetails details, DateTimeOffset? added, DateTimeOffset? finished) {
		InitializeComponent();
		var item = details.Item;

		Add(Loc.S("Props_Name", "Name"), item.Name);
		Add(Loc.S("Props_Status", "Status"), item.Status.ToString());
		Add(Loc.S("Props_Url", "URL"), details.Uris.Count > 0 ? details.Uris[0].Uri : item.FirstUri);
		Add(Loc.S("Props_Referer", "Referer"), details.Referer);
		Add(Loc.S("Props_Size", "Size"), item.TotalBytes > 0 ? Loc.F("Props_SizeBytes", "{0} ({1:N0} bytes)", ByteFormat.Size(item.TotalBytes), item.TotalBytes) : Loc.S("Props_Unknown", "Unknown"));
		Add(Loc.S("Props_Downloaded", "Downloaded"), ByteFormat.Size(item.CompletedBytes));
		Add(Loc.S("Props_SavedTo", "Saved to"), item.Path ?? item.Dir);
		Add(Loc.S("Props_Connections", "Connections"), details.Connections > 0 ? details.Connections.ToString() : null);
		Add(Loc.S("Props_SpeedLimit", "Speed limit"), details.SpeedLimitBytesPerSec > 0 ? ByteFormat.Speed(details.SpeedLimitBytesPerSec) : Loc.S("Props_None", "None"));
		Add(Loc.S("Props_InfoHash", "Info hash"), details.InfoHash);
		Add(Loc.S("Props_Checksum", "Checksum"), details.Checksum);
		Add(Loc.S("Props_Added", "Added"), added?.LocalDateTime.ToString("g"));
		Add(Loc.S("Props_Finished", "Finished"), finished?.LocalDateTime.ToString("g"));
		if (item.Status == DownloadStatus.Error) {
			var friendly = Aria2Errors.Describe(item.ErrorCode, item.ErrorMessage);
			Add(Loc.S("Props_Error", "Error"), item.ErrorMessage is { Length: > 0 } raw && raw != friendly ? $"{friendly} ({raw})" : friendly);
		}
	}

	private void Add(string label, string? value) {
		if (string.IsNullOrWhiteSpace(value)) {
			return;
		}
		var panel = new StackPanel { Spacing = 2 };
		panel.Children.Add(new TextBlock { Text = label, Opacity = 0.7, FontSize = 12 });
		panel.Children.Add(new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Normal });
		Fields.Children.Add(panel);
	}
}
