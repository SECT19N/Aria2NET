using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria2NET.ViewModels;

/// <summary>One row of the History page.</summary>
public sealed partial class HistoryRowViewModel(HistoryEntry entry) : ObservableObject {
	public HistoryEntry Entry { get; } = entry;

	public string Name => Entry.Name;

	public string SizeText => Entry.TotalBytes > 0 ? ByteFormat.Size(Entry.TotalBytes) : "—";

	public string StatusText => Entry.Status switch {
		HistoryStatus.Complete => "Completed",
		HistoryStatus.Error => "Failed",
		HistoryStatus.Removed => "Removed",
		HistoryStatus.Interrupted => "Interrupted",
		HistoryStatus.Paused => "Paused",
		HistoryStatus.Waiting => "Queued",
		_ => "Downloading",
	};

	/// <summary>Why it failed, in plain words; empty for everything else.</summary>
	public string StatusTip => Entry.Status switch {
		HistoryStatus.Error => Aria2Errors.Describe(Entry.ErrorCode, Entry.ErrorMessage),
		HistoryStatus.Interrupted => "The app stopped before this download finished and aria2 no longer has it.",
		_ => "",
	};

	/// <summary>Local time, or "—" when it has not happened.</summary>
	public string AddedText => Entry.AddedUtc.ToLocalTime().ToString("g");

	public string FinishedText => Entry.FinishedUtc is { } f ? f.ToLocalTime().ToString("g") : "—";

	public string Folder => Entry.Dir ?? "";

	public string AccessibleName => $"{Name}, {StatusText}, {SizeText}, added {AddedText}{(FileMissing ? ", file missing" : "")}";

	/// <summary>A finished download whose file is no longer where it was saved. Filled in after the list is shown.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(MissingText), nameof(AccessibleName))]
	public partial bool FileMissing { get; set; }

	public string MissingText => FileMissing ? "File missing" : "";
}
