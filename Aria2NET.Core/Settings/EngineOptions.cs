namespace Aria2NET.Core.Settings;

/// <summary>Maps <see cref="AppSettings"/> to aria2 option names. Used for the conf file at start and for <c>changeGlobalOption</c> afterwards.</summary>
public static class EngineOptions {
	public const string MaxConcurrent = "max-concurrent-downloads";
	public const string DownloadLimit = "max-overall-download-limit";
	public const string UploadLimit = "max-overall-upload-limit";

	/// <summary>The limits in force right now, in KiB/s (0 = unlimited): the alternative pair when alternative speeds are on.</summary>
	public static (int Download, int Upload) EffectiveLimits(AppSettings s) =>
		s.AltSpeedActive ? (s.AltDownloadLimitKiBps, s.AltUploadLimitKiBps) : (s.DownloadLimitKiBps, s.UploadLimitKiBps);

	/// <summary>Only the two overall speed limits; what the alt-speed toggle changes.</summary>
	public static IReadOnlyDictionary<string, string> SpeedOptions(AppSettings s) {
		var (down, up) = EffectiveLimits(s);
		return new Dictionary<string, string> {
			[DownloadLimit] = LimitValue(down),
			[UploadLimit] = LimitValue(up),
		};
	}

	/// <summary>Every option the settings pages control. All of them can be changed on a running engine.</summary>
	public static IReadOnlyDictionary<string, string> ToGlobalOptions(AppSettings s) {
		var options = new Dictionary<string, string> {
			[MaxConcurrent] = Math.Max(1, s.MaxConcurrentDownloads).ToString(),
			["max-connection-per-server"] = Math.Clamp(s.MaxConnectionsPerServer, 1, 16).ToString(),
			["split"] = Math.Max(1, s.Split).ToString(),
			["min-split-size"] = $"{Math.Clamp(s.MinSplitSizeMiB, 1, 1024)}M",
			["max-tries"] = Math.Max(0, s.MaxTries).ToString(),
			["retry-wait"] = Math.Clamp(s.RetryWaitSeconds, 0, 600).ToString(),
			["connect-timeout"] = Math.Max(1, s.ConnectTimeoutSeconds).ToString(),
			["timeout"] = Math.Max(1, s.TimeoutSeconds).ToString(),
			["file-allocation"] = s.FileAllocation.ToString().ToLowerInvariant(),
		};
		// An empty value cannot reset the option on a running engine, so send aria2's own agent explicitly.
		options["user-agent"] = string.IsNullOrWhiteSpace(s.UserAgent) ? DefaultUserAgent : s.UserAgent.Trim();
		foreach (var (key, value) in SpeedOptions(s)) {
			options[key] = value;
		}
		return options;
	}

	/// <summary>aria2 1.37's built-in agent (the version <c>tools/get-aria2.ps1</c> fetches).</summary>
	public const string DefaultUserAgent = "aria2/1.37.0";

	private static string LimitValue(int kibPerSecond) => kibPerSecond <= 0 ? "0" : $"{kibPerSecond}K";
}
