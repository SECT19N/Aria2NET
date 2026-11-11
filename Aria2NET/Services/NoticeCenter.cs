using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Aria2NET.Services;

public sealed record NoticeAction(string Text, Func<Task> Run);

/// <summary>A message for the banner under the title bar, with up to two buttons.</summary>
public sealed record Notice(string Title, string Message, InfoBarSeverity Severity, NoticeAction? Primary = null, NoticeAction? Secondary = null);

/// <summary>Messages from anywhere in the app (any thread) shown one at a time in the main window. Must be created on the UI thread.</summary>
public sealed partial class NoticeCenter : ObservableObject {
	private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
	private readonly Queue<Notice> _waiting = new();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNotice))]
	[NotifyPropertyChangedFor(nameof(HasPrimary))]
	[NotifyPropertyChangedFor(nameof(HasSecondary))]
	[NotifyPropertyChangedFor(nameof(Severity))]
	[NotifyPropertyChangedFor(nameof(Title))]
	[NotifyPropertyChangedFor(nameof(Message))]
	[NotifyPropertyChangedFor(nameof(PrimaryText))]
	[NotifyPropertyChangedFor(nameof(SecondaryText))]
	public partial Notice? Current { get; private set; }

	public bool HasNotice => Current is not null;
	public bool HasPrimary => Current?.Primary is not null;
	public bool HasSecondary => Current?.Secondary is not null;

	// Flat properties for the banner: x:Bind with a FallbackValue cannot produce an enum, and null paths are fragile.
	public InfoBarSeverity Severity => Current?.Severity ?? InfoBarSeverity.Informational;
	public string Title => Current?.Title ?? "";
	public string Message => Current?.Message ?? "";
	public string PrimaryText => Current?.Primary?.Text ?? "";
	public string SecondaryText => Current?.Secondary?.Text ?? "";

	public void Post(Notice notice) => _dispatcher.TryEnqueue(() => {
		if (Current is null) {
			Current = notice;
		} else {
			_waiting.Enqueue(notice);
		}
	});

	/// <summary>Closes the current message and shows the next one, if any.</summary>
	public void Dismiss() => Current = _waiting.Count > 0 ? _waiting.Dequeue() : null;
}
