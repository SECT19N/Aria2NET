namespace Aria2NET.Core.History;

/// <summary>Status values stored in the history table. The first six mirror aria2; <see cref="Interrupted"/> means the app lost track of it.</summary>
public static class HistoryStatus {
	public const string Active = "active";
	public const string Waiting = "waiting";
	public const string Paused = "paused";
	public const string Complete = "complete";
	public const string Error = "error";
	public const string Removed = "removed";
	public const string Interrupted = "interrupted";

	public static IReadOnlyList<string> All { get; } = [Active, Waiting, Paused, Complete, Error, Removed, Interrupted];

	public static bool IsUnfinished(string status) => status is Active or Waiting or Paused;
	public static bool IsTerminal(string status) => status is Complete or Error or Removed;
}

public sealed record HistoryEntry(
	long Id,
	string Gid,
	string? Uri,
	string? Dir,
	string? Path,
	string Name,
	string? Host,
	string Category,
	long TotalBytes,
	long CompletedBytes,
	string Status,
	int? ErrorCode,
	string? ErrorMessage,
	DateTime AddedUtc,
	DateTime? FinishedUtc,
	string? InfoHash = null,
	bool IsTorrent = false,
	long UploadedBytes = 0);

public enum HistorySort {
	Added,
	Finished,
	Name,
	Size,
	Status,
}

public sealed record HistoryQuery(
	string? Search = null,
	IReadOnlyList<string>? Statuses = null,
	DateTime? FromUtc = null,
	DateTime? ToUtc = null,
	string? Host = null,
	string? Category = null,
	HistorySort Sort = HistorySort.Added,
	bool Descending = true,
	int Limit = 2000,
	int Offset = 0);

public sealed record ReconcileResult(int Interrupted, int Relinked);
