using System.Text.Json;

namespace Aria2NET.Core.Settings;

/// <summary>Export, import and reset of the whole settings file.</summary>
public static class SettingsTransfer {
	/// <summary>An imported file larger than this is refused (a settings file is a few KB).</summary>
	public const int MaxImportBytes = 256 * 1024;

	/// <summary>Settings that describe this PC or this window rather than preferences: never exported, never replaced by an import or a reset.</summary>
	private static void KeepMachineState(AppSettings from, AppSettings to) {
		to.Window = from.Window;
		to.LastUsedFolder = from.LastUsedFolder;
		to.TorrentAssociationPrompted = from.TorrentAssociationPrompted;
		to.ProxyPasswordProtected = from.ProxyPasswordProtected;
	}

	/// <summary>The settings as JSON for another PC: without the proxy password (it is encrypted for this account only), window position and last folder.</summary>
	public static string Export(AppSettings s) {
		var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, AppSettingsJsonContext.Default.AppSettings), AppSettingsJsonContext.Default.AppSettings)!;
		copy.ProxyPasswordProtected = null;
		copy.Window = null;
		copy.LastUsedFolder = null;
		copy.TorrentAssociationPrompted = false;
		return JsonSerializer.Serialize(copy, AppSettingsJsonContext.Default.AppSettings);
	}

	/// <summary>
	/// Reads settings exported earlier. Numbers outside their range are brought into it; anything that could be harmful or is not
	/// understood (unknown choices, a bad proxy or tracker address, forbidden raw options, control characters in text) refuses the
	/// whole file. The result keeps this PC's own machine state from <paramref name="current"/>.
	/// </summary>
	public static (AppSettings? Settings, string? Error) Import(string json, AppSettings current) {
		if (json.Length > MaxImportBytes) {
			return (null, "This file is too large to be a settings file.");
		}
		AppSettings? imported;
		try {
			imported = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
		} catch (JsonException) {
			return (null, "This is not a settings file from Aria2NET.");
		}
		if (imported is null) {
			return (null, "This is not a settings file from Aria2NET.");
		}
		if (imported.SchemaVersion != 1) {
			return (null, "This settings file is from a different version of Aria2NET.");
		}
		var problem = ValidateChoices(imported)
			?? ValidateText(imported)
			?? EngineOptions.ValidateProxy(imported)
			?? EngineOptions.ValidateTorrent(imported)
			?? RawOptionRules.Validate(imported.RawOptions ?? []);
		if (problem is not null) {
			return (null, problem);
		}
		KeepMachineState(current, imported);
		return (Sanitize(imported), null);
	}

	/// <summary>Defaults for every preference; the machine state stays.</summary>
	public static AppSettings ResetAll(AppSettings current) {
		var fresh = new AppSettings();
		KeepMachineState(current, fresh);
		fresh.ProxyPasswordProtected = null; // the proxy is reset to none, so its password goes too
		return fresh;
	}

	private static string? ValidateChoices(AppSettings s) {
		if (!Enum.IsDefined(s.FileAllocation) || !Enum.IsDefined(s.Proxy) || !Enum.IsDefined(s.Encryption) || !Enum.IsDefined(s.Trackers)
			|| !Enum.IsDefined(s.Theme) || !Enum.IsDefined(s.Conflict) || !Enum.IsDefined(s.LogLevel)) {
			return "This file contains a choice that this version does not know.";
		}
		return null;
	}

	/// <summary>Free text ends up in aria2's config file, so a line break or other control character is refused outright.</summary>
	private static string? ValidateText(AppSettings s) {
		foreach (var text in new[] { s.DefaultDownloadFolder, s.UserAgent, s.ProxyAddress, s.ProxyUser, s.TrackerUrl }) {
			if (text is not null && (text.Length > 1000 || text.Any(char.IsControl))) {
				return "This file contains text that is too long or has characters that are not allowed.";
			}
		}
		return null;
	}

	/// <summary>Puts numbers into the ranges the Settings page allows.</summary>
	public static AppSettings Sanitize(AppSettings s) {
		s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 50);
		s.MaxConnectionsPerServer = Math.Clamp(s.MaxConnectionsPerServer, 1, 16);
		s.Split = Math.Clamp(s.Split, 1, 64);
		s.MinSplitSizeMiB = Math.Clamp(s.MinSplitSizeMiB, 1, 1024);
		s.MaxTries = Math.Clamp(s.MaxTries, 0, 100);
		s.RetryWaitSeconds = Math.Clamp(s.RetryWaitSeconds, 0, 600);
		s.ConnectTimeoutSeconds = Math.Clamp(s.ConnectTimeoutSeconds, 1, 600);
		s.TimeoutSeconds = Math.Clamp(s.TimeoutSeconds, 1, 600);
		s.DownloadLimitKiBps = Math.Clamp(s.DownloadLimitKiBps, 0, 10_000_000);
		s.UploadLimitKiBps = Math.Clamp(s.UploadLimitKiBps, 0, 10_000_000);
		s.AltDownloadLimitKiBps = Math.Clamp(s.AltDownloadLimitKiBps, 0, 10_000_000);
		s.AltUploadLimitKiBps = Math.Clamp(s.AltUploadLimitKiBps, 0, 10_000_000);
		s.SeedRatio = double.IsFinite(s.SeedRatio) ? Math.Clamp(s.SeedRatio, 0, 1000) : 1.0;
		s.SeedTimeMinutes = Math.Clamp(s.SeedTimeMinutes, 0, 5_256_000);
		s.HistoryRetentionDays = Math.Clamp(s.HistoryRetentionDays, 0, 3650);
		s.RawOptions ??= [];
		return s;
	}
}
