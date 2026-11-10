using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Aria2NET.Core.Aria2;

/// <summary>
/// Settings for one aria2c launch. Written to a conf file instead of the command line so the
/// RPC secret is not visible in the process list.
/// </summary>
public sealed record Aria2Config(int Port, string Secret, int ParentPid, string DownloadDir, IReadOnlyList<string>? Trackers = null, IReadOnlyDictionary<string, string>? Options = null) {
	public static Aria2Config CreateRandom(string downloadDir, int parentPid, IReadOnlyList<string>? trackers = null, IReadOnlyDictionary<string, string>? options = null) =>
		new(FindFreeLoopbackPort(), GenerateSecret(), parentPid, downloadDir, trackers, options);

	public static string GenerateSecret() =>
		Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

	public static int FindFreeLoopbackPort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally {
			listener.Stop();
		}
	}

	/// <summary>The value of <c>bt-tracker</c>: addresses joined by commas.</summary>
	public static string TrackerOptionValue(IEnumerable<string> trackers) => string.Join(",", trackers);

	public string Render(AppPaths paths) {
		var sb = new StringBuilder();
		sb.AppendLine("enable-rpc=true");
		sb.AppendLine($"rpc-listen-port={Port}");
		sb.AppendLine($"rpc-secret={Secret}");
		sb.AppendLine("rpc-listen-all=false");
		sb.AppendLine("rpc-allow-origin-all=false");
		sb.AppendLine($"stop-with-process={ParentPid}");
		sb.AppendLine($"dir={DownloadDir}");
		sb.AppendLine($"input-file={paths.Aria2SessionFile}");
		sb.AppendLine($"save-session={paths.Aria2SessionFile}");
		sb.AppendLine("save-session-interval=30");
		sb.AppendLine("continue=true");
		sb.AppendLine("console-log-level=warn");
		sb.AppendLine($"log={paths.Aria2LogFile}");
		sb.AppendLine("log-level=notice");
		// BitTorrent. aria2 writes <infohash>.torrent into the download folder (only if it exists) and reuses it when the same magnet is added again.
		sb.AppendLine("bt-save-metadata=true");
		sb.AppendLine("bt-load-saved-metadata=true");
		sb.AppendLine("enable-dht=true");
		sb.AppendLine("enable-dht6=true");
		sb.AppendLine("bt-enable-lpd=true");
		sb.AppendLine($"dht-file-path={Path.Combine(paths.EngineDir, "dht.dat")}");
		sb.AppendLine($"dht-file-path6={Path.Combine(paths.EngineDir, "dht6.dat")}");
		sb.AppendLine("seed-ratio=1.0");
		// Not falloc: without the "perform volume maintenance" privilege (any non-admin run) aria2 falls back to zero-filling the whole
		// file, which blocks the engine for minutes on a big torrent. Files grow as data arrives instead.
		sb.AppendLine("file-allocation=none");
		// The user's settings come last, so they win over the defaults above (aria2 uses the last occurrence).
		foreach (var (key, value) in Options ?? new Dictionary<string, string>()) {
			sb.AppendLine($"{key}={value}");
		}
		if (Trackers is { Count: > 0 }) {
			sb.AppendLine($"bt-tracker={TrackerOptionValue(Trackers)}");
		}
		return sb.ToString();
	}
}
