using Aria2NET.Core.Downloads;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Pages;

public sealed partial class PropertiesDialog : ContentDialog {
	public PropertiesDialog(DownloadDetails details, DateTimeOffset? added, DateTimeOffset? finished) {
		InitializeComponent();
		var item = details.Item;

		Add("Name", item.Name);
		Add("Status", item.Status.ToString());
		Add("URL", details.Uris.Count > 0 ? details.Uris[0].Uri : item.FirstUri);
		Add("Referer", details.Referer);
		Add("Size", item.TotalBytes > 0 ? $"{ByteFormat.Size(item.TotalBytes)} ({item.TotalBytes:N0} bytes)" : "Unknown");
		Add("Downloaded", ByteFormat.Size(item.CompletedBytes));
		Add("Saved to", item.Path ?? item.Dir);
		Add("Connections", details.Connections > 0 ? details.Connections.ToString() : null);
		Add("Speed limit", details.SpeedLimitBytesPerSec > 0 ? ByteFormat.Speed(details.SpeedLimitBytesPerSec) : "None");
		Add("Info hash", details.InfoHash);
		Add("Checksum", details.Checksum);
		Add("Added", added?.LocalDateTime.ToString("g"));
		Add("Finished", finished?.LocalDateTime.ToString("g"));
		if (item.Status == DownloadStatus.Error) {
			var friendly = Aria2Errors.Describe(item.ErrorCode, item.ErrorMessage);
			Add("Error", item.ErrorMessage is { Length: > 0 } raw && raw != friendly ? $"{friendly} ({raw})" : friendly);
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
