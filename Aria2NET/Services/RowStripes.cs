using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Aria2NET.Services;

/// <summary>Alternating row backgrounds for a ListView. A faint neutral tint reads on both light and dark themes and leaves hover and selection visuals alone.</summary>
internal static class RowStripes {
	private static readonly Brush Stripe = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x80));
	private static readonly Brush Plain = new SolidColorBrush(Colors.Transparent);

	/// <summary>Call from ContainerContentChanging so freshly realized or recycled rows get the right shade.</summary>
	public static void Apply(ContainerContentChangingEventArgs args) {
		if (args.ItemContainer is ListViewItem item) {
			item.Background = args.ItemIndex % 2 == 1 ? Stripe : Plain;
		}
	}

	/// <summary>Re-shades every realized row. Inserting or removing a row shifts the index of the rows after it without raising ContainerContentChanging for them.</summary>
	public static void Refresh(ListViewBase list) {
		for (int i = 0; i < list.Items.Count; i++) {
			if (list.ContainerFromIndex(i) is ListViewItem item) {
				item.Background = i % 2 == 1 ? Stripe : Plain;
			}
		}
	}

	/// <summary>Refreshes once the list has processed the change that triggered it.</summary>
	public static void RefreshLater(ListViewBase list) =>
		list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Refresh(list));
}
