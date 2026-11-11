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

	/// <summary>Options aria2 accepts in <c>changeGlobalOption</c> but ignores on a running engine; they take effect at the next launch.</summary>
	public static readonly IReadOnlyList<string> LaunchOnlyKeys = [
		"check-certificate", "disable-ipv6", "listen-port", "dht-listen-port", "enable-dht", "enable-dht6"];

	/// <summary>Minutes in ten years: "no time limit" for a torrent whose ratio rule alone should decide when seeding ends.</summary>
	public const string NoSeedTime = "5256000";

	/// <summary>What to send to a running engine: everything except <see cref="LaunchOnlyKeys"/>.</summary>
	public static IReadOnlyDictionary<string, string> LiveOptions(AppSettings s) =>
		ToGlobalOptions(s).Where(o => !LaunchOnlyKeys.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value);

	/// <summary>Every option the settings pages control, for the conf file at launch. Use <see cref="LiveOptions"/> for a running engine.</summary>
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
		options["user-agent"] = string.IsNullOrWhiteSpace(s.UserAgent) || s.UserAgent.Any(char.IsControl) ? DefaultUserAgent : s.UserAgent.Trim();
		foreach (var (key, value) in SpeedOptions(s)) {
			options[key] = value;
		}
		foreach (var (key, value) in NetworkOptions(s)) {
			options[key] = value;
		}
		foreach (var (key, value) in TorrentOptions(s)) {
			options[key] = value;
		}
		// New downloads only. "Ask" renames at the engine level; the question itself is asked before the download is added.
		options["allow-overwrite"] = s.Conflict == ConflictPolicy.Overwrite ? "true" : "false";
		options["auto-file-renaming"] = s.Conflict == ConflictPolicy.Overwrite ? "false" : "true";
		options["log-level"] = Aria2LogLevel(s.LogLevel);
		// Hand-typed options last, so they win. An option that fails the rules (say, from a hand-edited file) is dropped, never written to the conf.
		foreach (var (key, value) in s.RawOptions ?? []) {
			if (RawOptionRules.Validate(key, value) is null) {
				options[key] = value;
			}
		}
		return options;
	}

	/// <summary>aria2's name for an app log level (<c>notice</c> is aria2's default).</summary>
	public static string Aria2LogLevel(AppLogLevel level) => level switch {
		AppLogLevel.Warning => "warn",
		AppLogLevel.Debug => "debug",
		_ => "notice",
	};

	/// <summary>The matching level for the app's own log.</summary>
	public static Microsoft.Extensions.Logging.LogLevel AppLogLevelFor(AppLogLevel level) => level switch {
		AppLogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
		AppLogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
		_ => Microsoft.Extensions.Logging.LogLevel.Information,
	};

	/// <summary>Ports, DHT, peer discovery, encryption and the default seeding rule. An empty <c>seed-time</c> clears the time limit.</summary>
	public static IReadOnlyDictionary<string, string> TorrentOptions(AppSettings s) {
		var (portStart, portEnd) = ListenPorts(s);
		var arc4 = s.Encryption != TorrentEncryption.Allow;
		return new Dictionary<string, string> {
			["listen-port"] = portStart == portEnd ? portStart.ToString() : $"{portStart}-{portEnd}",
			["dht-listen-port"] = Math.Clamp(s.DhtPort, 1024, 65535).ToString(),
			["enable-dht"] = s.EnableDht ? "true" : "false",
			["enable-dht6"] = s.EnableDht && s.UseIPv6 ? "true" : "false",
			["enable-peer-exchange"] = s.EnablePex ? "true" : "false",
			["bt-enable-lpd"] = s.EnableLpd ? "true" : "false",
			["bt-min-crypto-level"] = arc4 ? "arc4" : "plain",
			["bt-require-crypto"] = s.Encryption == TorrentEncryption.Require ? "true" : "false",
			["seed-ratio"] = Math.Max(0, s.SeedRatio).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture),
			["seed-time"] = s.SeedTimeMinutes > 0 ? s.SeedTimeMinutes.ToString() : "",
		};
	}

	/// <summary>The listen port range, clamped to 1024-65535 and put in order.</summary>
	public static (int Start, int End) ListenPorts(AppSettings s) {
		var start = Math.Clamp(s.ListenPortStart, 1024, 65535);
		var end = Math.Clamp(s.ListenPortEnd, 1024, 65535);
		return (Math.Min(start, end), Math.Max(start, end));
	}

	/// <summary>Null when the torrent settings are usable; otherwise a short reason.</summary>
	public static string? ValidateTorrent(AppSettings s) {
		if (s.ListenPortStart is < 1024 or > 65535 || s.ListenPortEnd is < 1024 or > 65535 || s.DhtPort is < 1024 or > 65535) {
			return "Ports must be between 1024 and 65535.";
		}
		if (s.ListenPortStart > s.ListenPortEnd) {
			return "The first listen port must not be higher than the last one.";
		}
		if (s.Trackers == TrackerSource.Custom) {
			if (!Uri.TryCreate(s.TrackerUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)) {
				return "The tracker list address must be a full https:// address.";
			}
		}
		return null;
	}

	/// <summary>Proxy, certificate and IPv6 options. Empty proxy values clear a proxy set earlier.</summary>
	public static IReadOnlyDictionary<string, string> NetworkOptions(AppSettings s, Func<Uri?>? systemProxy = null) {
		var proxy = "";
		var user = "";
		var password = "";
		switch (s.Proxy) {
			case ProxyMode.Manual when ValidateProxy(s) is null: // an address that is not valid yet is ignored, not sent
				proxy = (s.ProxyAddress ?? "").Trim();
				user = s.ProxyUser?.Trim() ?? "";
				password = SecretProtector.Unprotect(s.ProxyPasswordProtected);
				break;
			case ProxyMode.System:
				proxy = (systemProxy ?? SystemProxyUri)()?.GetLeftPart(UriPartial.Authority) ?? "";
				break;
		}
		return new Dictionary<string, string> {
			["all-proxy"] = proxy,
			["all-proxy-user"] = user,
			["all-proxy-passwd"] = password,
			["check-certificate"] = s.CheckCertificates ? "true" : "false",
			["disable-ipv6"] = s.UseIPv6 ? "false" : "true",
		};
	}

	/// <summary>The proxy Windows would use for an ordinary web address, or null for a direct connection.</summary>
	private static Uri? SystemProxyUri() {
		try {
			var target = new Uri("https://example.com/");
			var proxy = HttpClient.DefaultProxy;
			return proxy.IsBypassed(target) ? null : proxy.GetProxy(target);
		} catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or PlatformNotSupportedException) {
			return null;
		}
	}

	/// <summary>Null when the settings are usable; otherwise a short reason for the Network section.</summary>
	public static string? ValidateProxy(AppSettings s) {
		if (s.Proxy != ProxyMode.Manual) {
			return null;
		}
		var address = s.ProxyAddress?.Trim();
		if (string.IsNullOrEmpty(address)) {
			return "Enter the proxy address, for example proxy.example.com:8080.";
		}
		var withScheme = address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address;
		if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535) {
			return "The proxy address is not valid. Use host:port, for example proxy.example.com:8080.";
		}
		return null;
	}

	/// <summary>aria2 1.37's built-in agent (the version <c>tools/get-aria2.ps1</c> fetches).</summary>
	public const string DefaultUserAgent = "aria2/1.37.0";

	private static string LimitValue(int kibPerSecond) => kibPerSecond <= 0 ? "0" : $"{kibPerSecond}K";
}
