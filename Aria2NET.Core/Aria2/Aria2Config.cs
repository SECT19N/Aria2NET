using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Aria2NET.Core.Aria2;

/// <summary>
/// Settings for one aria2c launch. Written to a conf file instead of the command line so the
/// RPC secret is not visible in the process list.
/// </summary>
public sealed record Aria2Config(int Port, string Secret, int ParentPid, string DownloadDir) {
	public static Aria2Config CreateRandom(string downloadDir, int parentPid) =>
		new(FindFreeLoopbackPort(), GenerateSecret(), parentPid, downloadDir);

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
		return sb.ToString();
	}
}
