using System.Globalization;
using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.History;

/// <summary>Every download the app has seen, kept in SQLite so it outlives aria2's own (in-memory) result list.</summary>
public sealed class HistoryStore(Database database, ILogger? logger = null) {
	private const string Columns = "id, gid, uri, dir, path, name, host, category, total_bytes, completed_bytes, status, error_code, error_message, added_utc, finished_utc";

	public void Upsert(IEnumerable<DownloadItem> items, DateTime nowUtc) {
		using var connection = database.Open();
		using var tx = connection.BeginTransaction();
		UpsertCore(connection, tx, items, nowUtc);
		tx.Commit();
	}

	private static void UpsertCore(SqliteConnection connection, SqliteTransaction tx, IEnumerable<DownloadItem> items, DateTime nowUtc) {
		using var command = connection.CreateCommand();
		command.Transaction = tx;
		command.CommandText = """
			INSERT INTO downloads (gid, uri, dir, path, name, host, category, total_bytes, completed_bytes, status, error_code, error_message, added_utc, finished_utc, updated_utc)
			VALUES ($gid, $uri, $dir, $path, $name, $host, $category, $total, $completed, $status, $code, $message, $now, $finished, $now)
			ON CONFLICT(gid) DO UPDATE SET
				uri = COALESCE(excluded.uri, uri),
				dir = COALESCE(excluded.dir, dir),
				path = COALESCE(excluded.path, path),
				name = excluded.name,
				host = COALESCE(excluded.host, host),
				category = excluded.category,
				total_bytes = excluded.total_bytes,
				completed_bytes = excluded.completed_bytes,
				status = excluded.status,
				error_code = excluded.error_code,
				error_message = excluded.error_message,
				finished_utc = CASE WHEN excluded.status IN ('complete', 'error', 'removed') THEN COALESCE(finished_utc, excluded.finished_utc) ELSE NULL END,
				updated_utc = excluded.updated_utc
			""";
		var gid = command.Parameters.Add("$gid", SqliteType.Text);
		var uri = command.Parameters.Add("$uri", SqliteType.Text);
		var dir = command.Parameters.Add("$dir", SqliteType.Text);
		var path = command.Parameters.Add("$path", SqliteType.Text);
		var name = command.Parameters.Add("$name", SqliteType.Text);
		var host = command.Parameters.Add("$host", SqliteType.Text);
		var category = command.Parameters.Add("$category", SqliteType.Text);
		var total = command.Parameters.Add("$total", SqliteType.Integer);
		var completed = command.Parameters.Add("$completed", SqliteType.Integer);
		var status = command.Parameters.Add("$status", SqliteType.Text);
		var code = command.Parameters.Add("$code", SqliteType.Integer);
		var message = command.Parameters.Add("$message", SqliteType.Text);
		var now = command.Parameters.Add("$now", SqliteType.Text);
		var finished = command.Parameters.Add("$finished", SqliteType.Text);

		var nowText = Format(nowUtc);
		foreach (var item in items) {
			var statusText = item.Status.ToString().ToLowerInvariant();
			gid.Value = item.Gid;
			uri.Value = (object?)item.FirstUri ?? DBNull.Value;
			dir.Value = (object?)item.Dir ?? DBNull.Value;
			path.Value = (object?)item.Path ?? DBNull.Value;
			name.Value = item.Name;
			host.Value = (object?)HostOf(item.FirstUri) ?? DBNull.Value;
			category.Value = FileCategory.FromName(item.Name);
			total.Value = item.TotalBytes;
			completed.Value = item.CompletedBytes;
			status.Value = statusText;
			code.Value = item.ErrorCode is { } c ? c : DBNull.Value;
			message.Value = (object?)item.ErrorMessage ?? DBNull.Value;
			now.Value = nowText;
			finished.Value = HistoryStatus.IsTerminal(statusText) ? nowText : DBNull.Value;
			command.ExecuteNonQuery();
		}
	}

	/// <summary>Marks downloads that disappeared from aria2 while still unfinished (removed by the user).</summary>
	public void MarkRemoved(IEnumerable<string> gids, DateTime nowUtc) {
		using var connection = database.Open();
		using var tx = connection.BeginTransaction();
		using var command = connection.CreateCommand();
		command.Transaction = tx;
		command.CommandText = "UPDATE downloads SET status = 'removed', finished_utc = COALESCE(finished_utc, $now), updated_utc = $now WHERE gid = $gid AND status IN ('active', 'waiting', 'paused')";
		var now = command.Parameters.Add("$now", SqliteType.Text);
		var gid = command.Parameters.Add("$gid", SqliteType.Text);
		now.Value = Format(nowUtc);
		foreach (var g in gids) {
			gid.Value = g;
			command.ExecuteNonQuery();
		}
		tx.Commit();
	}

	/// <summary>
	/// Run once per engine start with everything aria2 reports. Unfinished rows aria2 no longer knows are matched by URL and folder
	/// to a download it does know (its GID changed), otherwise marked interrupted. Then the live items are recorded.
	/// </summary>
	public ReconcileResult Reconcile(IReadOnlyList<DownloadItem> live, DateTime nowUtc) {
		using var connection = database.Open();
		using var tx = connection.BeginTransaction();

		var known = new HashSet<string>(StringComparer.Ordinal);
		var unfinished = new List<(long Id, string Gid, string? Uri, string? Dir)>();
		using (var select = connection.CreateCommand()) {
			select.Transaction = tx;
			select.CommandText = "SELECT id, gid, uri, dir, status FROM downloads";
			using var reader = select.ExecuteReader();
			while (reader.Read()) {
				var rowGid = reader.GetString(1);
				known.Add(rowGid);
				if (HistoryStatus.IsUnfinished(reader.GetString(4))) {
					unfinished.Add((reader.GetInt64(0), rowGid, reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
				}
			}
		}

		var liveGids = live.Select(i => i.Gid).ToHashSet(StringComparer.Ordinal);
		var strangers = live.Where(i => !known.Contains(i.Gid)).ToList();
		var interrupted = 0;
		var relinked = 0;
		var nowText = Format(nowUtc);
		foreach (var row in unfinished.Where(r => !liveGids.Contains(r.Gid))) {
			var match = strangers.FirstOrDefault(i => row.Uri is not null && i.FirstUri == row.Uri && string.Equals(i.Dir, row.Dir, StringComparison.OrdinalIgnoreCase));
			using var update = connection.CreateCommand();
			update.Transaction = tx;
			if (match is not null) {
				strangers.Remove(match);
				update.CommandText = "UPDATE downloads SET gid = $new, updated_utc = $now WHERE id = $id";
				update.Parameters.AddWithValue("$new", match.Gid);
				relinked++;
			}
			else {
				update.CommandText = "UPDATE downloads SET status = 'interrupted', updated_utc = $now WHERE id = $id";
				interrupted++;
			}
			update.Parameters.AddWithValue("$now", nowText);
			update.Parameters.AddWithValue("$id", row.Id);
			update.ExecuteNonQuery();
		}

		UpsertCore(connection, tx, live, nowUtc);
		tx.Commit();
		return new ReconcileResult(interrupted, relinked);
	}

	public IReadOnlyList<HistoryEntry> Query(HistoryQuery query) {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		var (where, bind) = BuildWhere(query);
		var order = query.Sort switch {
			HistorySort.Finished => "COALESCE(finished_utc, added_utc)",
			HistorySort.Name => "name COLLATE NOCASE",
			HistorySort.Size => "total_bytes",
			HistorySort.Status => "status",
			_ => "added_utc",
		};
		var direction = query.Descending ? "DESC" : "ASC";
		command.CommandText = $"SELECT {Columns} FROM downloads {where} ORDER BY {order} {direction}, id {direction} LIMIT $limit OFFSET $offset";
		bind(command);
		command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100_000));
		command.Parameters.AddWithValue("$offset", Math.Max(query.Offset, 0));
		return ReadAll(command);
	}

	public int Count(HistoryQuery? query = null) {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		var (where, bind) = BuildWhere(query ?? new HistoryQuery());
		command.CommandText = $"SELECT COUNT(*) FROM downloads {where}";
		bind(command);
		return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	public HistoryEntry? Get(string gid) {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		command.CommandText = $"SELECT {Columns} FROM downloads WHERE gid = $gid";
		command.Parameters.AddWithValue("$gid", gid);
		return ReadAll(command).FirstOrDefault();
	}

	/// <summary>Domains seen so far, for the History filter.</summary>
	public IReadOnlyList<string> Hosts() {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT DISTINCT host FROM downloads WHERE host IS NOT NULL AND host <> '' ORDER BY host";
		using var reader = command.ExecuteReader();
		var result = new List<string>();
		while (reader.Read()) {
			result.Add(reader.GetString(0));
		}
		return result;
	}

	/// <summary>Folders downloads went to, newest first, for scanning leftovers.</summary>
	public IReadOnlyList<string> Folders(int limit = 50) {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "SELECT dir FROM downloads WHERE dir IS NOT NULL AND dir <> '' GROUP BY dir ORDER BY MAX(added_utc) DESC LIMIT $limit";
		command.Parameters.AddWithValue("$limit", limit);
		using var reader = command.ExecuteReader();
		var result = new List<string>();
		while (reader.Read()) {
			result.Add(reader.GetString(0));
		}
		return result;
	}

	/// <summary>Newest history row whose file is <paramref name="path"/> (compared without regard to case).</summary>
	public HistoryEntry? FindByPath(string path) {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		command.CommandText = $"SELECT {Columns} FROM downloads WHERE path = $path COLLATE NOCASE ORDER BY added_utc DESC LIMIT 1";
		command.Parameters.AddWithValue("$path", path);
		return ReadAll(command).FirstOrDefault();
	}

	public int Delete(IEnumerable<long> ids) {
		using var connection = database.Open();
		using var tx = connection.BeginTransaction();
		using var command = connection.CreateCommand();
		command.Transaction = tx;
		command.CommandText = "DELETE FROM downloads WHERE id = $id";
		var id = command.Parameters.Add("$id", SqliteType.Integer);
		var count = 0;
		foreach (var value in ids) {
			id.Value = value;
			count += command.ExecuteNonQuery();
		}
		tx.Commit();
		return count;
	}

	/// <summary>Removes everything that is finished one way or another. Downloads still in progress stay.</summary>
	public int Clear() => ExecuteDelete("DELETE FROM downloads WHERE status NOT IN ('active', 'waiting', 'paused')", null);

	/// <summary>Deletes finished rows older than <paramref name="cutoffUtc"/>. Anything still unfinished is kept.</summary>
	public int Purge(DateTime cutoffUtc) =>
		ExecuteDelete("DELETE FROM downloads WHERE status NOT IN ('active', 'waiting', 'paused') AND COALESCE(finished_utc, added_utc) < $cutoff", ("$cutoff", Format(cutoffUtc)));

	private int ExecuteDelete(string sql, (string Name, object Value)? parameter) {
		using var connection = database.Open();
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		if (parameter is { } p) {
			command.Parameters.AddWithValue(p.Name, p.Value);
		}
		var count = command.ExecuteNonQuery();
		logger?.LogInformation("History: deleted {Count} rows", count);
		return count;
	}

	private static (string Where, Action<SqliteCommand> Bind) BuildWhere(HistoryQuery q) {
		var clauses = new List<string>();
		var binds = new List<Action<SqliteCommand>>();

		if (!string.IsNullOrWhiteSpace(q.Search)) {
			clauses.Add("(name LIKE $search ESCAPE '\\' OR uri LIKE $search ESCAPE '\\' OR path LIKE $search ESCAPE '\\')");
			var pattern = "%" + q.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
			binds.Add(c => c.Parameters.AddWithValue("$search", pattern));
		}
		if (q.Statuses is { Count: > 0 }) {
			var names = q.Statuses.Select((_, i) => "$st" + i).ToList();
			clauses.Add($"status IN ({string.Join(", ", names)})");
			binds.Add(c => {
				for (var i = 0; i < names.Count; i++) {
					c.Parameters.AddWithValue(names[i], q.Statuses[i]);
				}
			});
		}
		if (q.FromUtc is { } from) {
			clauses.Add("added_utc >= $from");
			binds.Add(c => c.Parameters.AddWithValue("$from", Format(from)));
		}
		if (q.ToUtc is { } to) {
			clauses.Add("added_utc < $to");
			binds.Add(c => c.Parameters.AddWithValue("$to", Format(to)));
		}
		if (!string.IsNullOrWhiteSpace(q.Host)) {
			clauses.Add("host = $host");
			binds.Add(c => c.Parameters.AddWithValue("$host", q.Host.Trim().ToLowerInvariant()));
		}
		if (!string.IsNullOrWhiteSpace(q.Category)) {
			clauses.Add("category = $category");
			binds.Add(c => c.Parameters.AddWithValue("$category", q.Category));
		}

		var where = clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
		return (where, c => binds.ForEach(b => b(c)));
	}

	private static List<HistoryEntry> ReadAll(SqliteCommand command) {
		var result = new List<HistoryEntry>();
		using var reader = command.ExecuteReader();
		while (reader.Read()) {
			result.Add(new HistoryEntry(
				reader.GetInt64(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), Text(reader, 4), reader.GetString(5), Text(reader, 6), reader.GetString(7),
				reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10),
				reader.IsDBNull(11) ? null : reader.GetInt32(11), Text(reader, 12),
				Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : Parse(reader.GetString(14))));
		}
		return result;
	}

	private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

	internal static string Format(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

	private static DateTime Parse(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

	internal static string? HostOf(string? uri) =>
		uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host)
			? parsed.Host.ToLowerInvariant()
			: null;
}
