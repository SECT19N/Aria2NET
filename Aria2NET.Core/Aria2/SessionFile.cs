using System.Text;
using Aria2NET.Core.Data;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Aria2;

public enum SessionHealth {
	Missing,
	Empty,
	Ok,
	/// <summary>Not valid text (zero bytes or broken UTF-8), as left by a crash or power loss while writing.</summary>
	Corrupt,
}

public sealed record SessionPrepareResult(bool Repaired, string? RestoredFrom, string? MovedTo);

/// <summary>Keeps rotating copies of aria2's session file and repairs it from one when it is damaged.</summary>
public static class SessionFile {
	public const string BackupPrefix = "aria2.session";
	public const int KeepBackups = 5;

	private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

	public static SessionHealth Check(string path) {
		if (!File.Exists(path)) {
			return SessionHealth.Missing;
		}
		byte[] bytes;
		try {
			bytes = File.ReadAllBytes(path);
		} catch (IOException) {
			return SessionHealth.Corrupt;
		}
		if (bytes.Length == 0) {
			return SessionHealth.Empty;
		}
		if (Array.IndexOf(bytes, (byte) 0) >= 0) {
			return SessionHealth.Corrupt;
		}
		try {
			Strict.GetString(bytes);
		} catch (DecoderFallbackException) {
			return SessionHealth.Corrupt;
		}
		return SessionHealth.Ok;
	}

	/// <summary>
	/// Called before every aria2 start: backs up a healthy session file (skipping a copy identical to the newest one), replaces a damaged
	/// one with the newest healthy backup, and makes sure a file exists so aria2 does not warn about a missing --input-file.
	/// </summary>
	public static SessionPrepareResult PrepareForStart(AppPaths paths, ILogger? logger = null) {
		var file = paths.Aria2SessionFile;
		var health = Check(file);
		switch (health) {
			case SessionHealth.Ok:
				BackUp(file, paths.BackupsDir);
				return new SessionPrepareResult(false, null, null);

			case SessionHealth.Corrupt:
				var moved = $"{file}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
				File.Move(file, moved, overwrite: true);
				var restored = BackupRotation.Newest(paths.BackupsDir, BackupPrefix).FirstOrDefault(b => Check(b) == SessionHealth.Ok);
				if (restored is not null) {
					File.Copy(restored, file, overwrite: true);
					logger?.LogWarning("aria2 session file was damaged; kept it as {Moved} and restored {Backup}", moved, restored);
				} else {
					File.WriteAllBytes(file, []);
					logger?.LogWarning("aria2 session file was damaged and no backup was usable; kept it as {Moved} and started empty", moved);
				}
				return new SessionPrepareResult(true, restored, moved);

			default:
				File.WriteAllBytes(file, []);
				return new SessionPrepareResult(false, null, null);
		}
	}

	private static void BackUp(string file, string backupsDir) {
		var newest = BackupRotation.Newest(backupsDir, BackupPrefix).FirstOrDefault();
		if (newest is not null && File.ReadAllBytes(newest).AsSpan().SequenceEqual(File.ReadAllBytes(file))) {
			return;
		}
		BackupRotation.CopyAndPrune(file, backupsDir, BackupPrefix, KeepBackups);
	}
}
