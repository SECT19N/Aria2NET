namespace Aria2NET.Core.Data;

public sealed record Migration(int Version, string Sql);

/// <summary>
/// The schema, one step per version. Applied in order on startup; <c>PRAGMA user_version</c> holds the version reached.
/// Never edit a shipped migration: add a new one.
/// </summary>
public static class Migrations {
	public static IReadOnlyList<Migration> All { get; } = [
		new(1, """
			CREATE TABLE downloads (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				gid TEXT NOT NULL UNIQUE,
				uri TEXT,
				dir TEXT,
				path TEXT,
				name TEXT NOT NULL,
				host TEXT,
				category TEXT NOT NULL DEFAULT 'Other',
				total_bytes INTEGER NOT NULL DEFAULT 0,
				completed_bytes INTEGER NOT NULL DEFAULT 0,
				status TEXT NOT NULL,
				error_code INTEGER,
				error_message TEXT,
				added_utc TEXT NOT NULL,
				finished_utc TEXT,
				updated_utc TEXT NOT NULL
			);
			CREATE INDEX ix_downloads_added ON downloads(added_utc);
			CREATE INDEX ix_downloads_status ON downloads(status);
			"""),
		// 0.5 torrents: the info hash identifies a torrent across GID changes and is what duplicate detection looks up.
		new(2, """
			ALTER TABLE downloads ADD COLUMN info_hash TEXT;
			ALTER TABLE downloads ADD COLUMN is_torrent INTEGER NOT NULL DEFAULT 0;
			ALTER TABLE downloads ADD COLUMN uploaded_bytes INTEGER NOT NULL DEFAULT 0;
			CREATE INDEX ix_downloads_hash ON downloads(info_hash);
			"""),
	];

	public static int Latest => All[^1].Version;
}
