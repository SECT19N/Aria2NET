using Aria2NET.Core.Data;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.History;

/// <summary>
/// Opens the history database once at startup. If that fails the app keeps working without history:
/// <see cref="Store"/> is null and <see cref="Problem"/> says why.
/// </summary>
public sealed class HistoryService {
	public HistoryStore? Store { get; }
	public HistoryRecorder? Recorder { get; }
	public DatabaseOpenResult? OpenResult { get; }
	public string? Problem { get; }

	public bool IsAvailable => Store is not null;

	public HistoryService(AppPaths paths, ILogger logger) {
		try {
			var database = new Database(paths.DatabaseFile, paths.BackupsDir, logger);
			OpenResult = database.EnsureReady();
			if (OpenResult.Status == DatabaseStatus.TooNew) {
				Problem = "The history file was written by a newer version of Aria2NET, so history is switched off. It was left untouched.";
				return;
			}
			Store = new HistoryStore(database, logger);
			Recorder = new HistoryRecorder(Store, logger);
		}
		catch (Exception ex) {
			logger.LogError(ex, "History database could not be opened; history is off");
			Problem = "The history database could not be opened, so history is switched off. Details are in the log file.";
		}
	}

	/// <summary>Deletes finished downloads older than the retention setting. 0 days keeps everything.</summary>
	public int ApplyRetention(int retentionDays, DateTime? nowUtc = null) =>
		Store is not null && retentionDays > 0 ? Store.Purge((nowUtc ?? DateTime.UtcNow).AddDays(-retentionDays)) : 0;
}
