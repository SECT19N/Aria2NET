using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Data;

public enum DatabaseStatus {
	Ready,
	/// <summary>The file was unreadable; it was moved aside and a new database was created.</summary>
	Recovered,
	/// <summary>The file was written by a newer version of the app. It is left untouched and history is switched off.</summary>
	TooNew,
}

public sealed record DatabaseOpenResult(DatabaseStatus Status, string? MovedTo = null, string? BackupMade = null);

/// <summary>SQLite file with versioned migrations, a backup before every schema change and a backup on every successful start.</summary>
public sealed class Database(string path, string backupsDir, ILogger? logger = null, IReadOnlyList<Migration>? migrations = null, int startBackups = 5) {
	private readonly IReadOnlyList<Migration> _migrations = migrations ?? Migrations.All;
	private readonly string _connectionString = new SqliteConnectionStringBuilder {
		DataSource = path,
		Mode = SqliteOpenMode.ReadWriteCreate,
		// Pooled connections would keep the file open after Dispose, which blocks moving it aside and deleting it.
		Pooling = false,
		DefaultTimeout = 10,
	}.ToString();

	public string Path => path;

	public SqliteConnection Open() {
		var connection = new SqliteConnection(_connectionString);
		try {
			connection.Open();
			using var pragma = connection.CreateCommand();
			pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
			pragma.ExecuteNonQuery();
			return connection;
		} catch {
			connection.Dispose();
			throw;
		}
	}

	/// <summary>Opens the file, repairing what can be repaired, and brings it to the newest schema.</summary>
	public DatabaseOpenResult EnsureReady() {
		try {
			return Prepare(movedTo: null);
		} catch (SqliteException ex) when (IsCorrupt(ex)) {
			var moved = MoveAside();
			logger?.LogError(ex, "History database unreadable; moved to {Path} and starting a new one", moved);
			return Prepare(moved);
		}
	}

	private DatabaseOpenResult Prepare(string? movedTo) {
		using var connection = Open();
		var current = ReadVersion(connection);
		if (current > _migrations[^1].Version) {
			logger?.LogError("History database is version {Found}, newer than this app understands ({Known}); leaving it alone", current, _migrations[^1].Version);
			return new DatabaseOpenResult(DatabaseStatus.TooNew);
		}

		string? backup = null;
		var pending = _migrations.Where(m => m.Version > current).OrderBy(m => m.Version).ToList();
		if (pending.Count > 0 && current > 0) {
			backup = Backup(connection, $"history-v{current}", keep: 5);
			logger?.LogInformation("Backed up history database to {Backup} before migrating from v{From}", backup, current);
		}
		foreach (var migration in pending) {
			using var tx = connection.BeginTransaction();
			using (var command = connection.CreateCommand()) {
				command.Transaction = tx;
				command.CommandText = migration.Sql;
				command.ExecuteNonQuery();
			}
			using (var setVersion = connection.CreateCommand()) {
				setVersion.Transaction = tx;
				setVersion.CommandText = $"PRAGMA user_version = {migration.Version}";
				setVersion.ExecuteNonQuery();
			}
			tx.Commit();
			logger?.LogInformation("History database migrated to v{Version}", migration.Version);
		}

		var startBackup = Backup(connection, "history-start", startBackups);
		return new DatabaseOpenResult(movedTo is null ? DatabaseStatus.Ready : DatabaseStatus.Recovered, movedTo, backup ?? startBackup);
	}

	/// <summary>Consistent copy of the live database (also correct while it is in WAL mode).</summary>
	public string Backup(SqliteConnection connection, string prefix, int keep) {
		var target = BackupRotation.NewPath(backupsDir, prefix, ".db");
		using var command = connection.CreateCommand();
		command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}'";
		command.ExecuteNonQuery();
		BackupRotation.Prune(backupsDir, prefix, keep);
		return target;
	}

	private static int ReadVersion(SqliteConnection connection) {
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA user_version";
		return Convert.ToInt32(command.ExecuteScalar());
	}

	// SQLITE_CORRUPT (11) and SQLITE_NOTADB (26).
	private static bool IsCorrupt(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

	private string MoveAside() {
		var target = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
		File.Move(path, target, overwrite: true);
		foreach (var suffix in new[] { "-wal", "-shm" }) {
			if (File.Exists(path + suffix)) {
				File.Delete(path + suffix);
			}
		}
		return target;
	}
}
