using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Aria2NET.Core.Torrents;

namespace Aria2NET.Tests;

/// <summary>A generated .torrent plus the payload it describes.</summary>
internal sealed record TestTorrent(string Name, byte[] TorrentFile, string InfoHash, IReadOnlyList<(string Path, byte[] Data)> Files) {
	public const int PieceLength = 16 * 1024;

	/// <summary>Builds a torrent over random data. One file gives a single-file torrent, several give a multi-file one rooted at <paramref name="name"/>.</summary>
	public static TestTorrent Create(string name, string announce, params (string Path, int Size)[] files) {
		var data = files.Select(f => (f.Path, Data: RandomNumberGenerator.GetBytes(f.Size))).ToList();
		var all = data.SelectMany(f => f.Data).ToArray();
		var pieces = new List<byte>();
		for (var i = 0; i < all.Length; i += PieceLength) {
			pieces.AddRange(SHA1.HashData(all.AsSpan(i, Math.Min(PieceLength, all.Length - i))));
		}

		var info = new Dictionary<string, object> {
			["name"] = name,
			["piece length"] = (long) PieceLength,
			["pieces"] = pieces.ToArray(),
		};
		if (files.Length == 1) {
			info["length"] = (long) files[0].Size;
		} else {
			info["files"] = data.Select(f => (object) new Dictionary<string, object> {
				["length"] = (long) f.Data.Length,
				["path"] = f.Path.Split('/').Select(p => (object) p).ToList(),
			}).ToList();
		}

		var root = new Dictionary<string, object> { ["announce"] = announce, ["info"] = info };
		var file = Bencode.Encode(root);
		var hash = Convert.ToHexString(SHA1.HashData(Bencode.FindRawTopLevelValue(file, "info")!)).ToLowerInvariant();
		return new TestTorrent(name, file, hash, data);
	}

	public string Magnet(string announce) => $"magnet:?xt=urn:btih:{InfoHash}&dn={Uri.EscapeDataString(Name)}&tr={Uri.EscapeDataString(announce)}";

	/// <summary>Writes the payload where a seeder expects to find it and returns the folder to pass as <c>dir</c>.</summary>
	public string WritePayload(string dir) {
		foreach (var (path, data) in Files) {
			var full = Files.Count == 1 ? Path.Combine(dir, Name) : Path.Combine(dir, Name, path.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllBytes(full, data);
		}
		return dir;
	}
}

/// <summary>Tracker that answers every announce with one fixed peer, so a leecher finds the local seeder without DHT.</summary>
internal sealed class LocalTracker : IDisposable {
	private readonly HttpListener _listener;

	public string AnnounceUrl { get; }
	public int Announces { get; private set; }

	public LocalTracker(int seederPort) {
		_listener = DownloadIntegrationTests.StartServer(out var prefix, async ctx => {
			Announces++;
			var peer = new List<byte>([127, 0, 0, 1]);
			peer.AddRange([(byte) (seederPort >> 8), (byte) (seederPort & 0xFF)]);
			var body = Bencode.Encode(new Dictionary<string, object> {
				["interval"] = 60L,
				["complete"] = 1L,
				["incomplete"] = 1L,
				["peers"] = peer.ToArray(),
			});
			ctx.Response.ContentLength64 = body.Length;
			await ctx.Response.OutputStream.WriteAsync(body);
			ctx.Response.Close();
		});
		AnnounceUrl = prefix + "announce";
	}

	public void Dispose() => _listener.Close();
}

/// <summary>A second aria2c that seeds already-complete data on a fixed loopback port. Stands in for the swarm.</summary>
internal sealed class SeederProcess : IDisposable {
	private readonly Process _process;
	private readonly TempDir _exeDir = new();

	public int Port { get; }

	public static int FreePort() {
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint) probe.LocalEndpoint).Port;
	}

	/// <param name="torrentPath">The .torrent file on disk.</param>
	/// <param name="payloadDir">Folder that holds the finished data (what <see cref="TestTorrent.WritePayload"/> returned).</param>
	/// <param name="port">Port the seeder listens on; the tracker must hand out the same one.</param>
	public SeederProcess(string torrentPath, string payloadDir, int port, string? uploadLimit = null) {
		Port = port;
		var args = new[] {
			"--no-conf", "--enable-rpc=false", "--enable-dht=false", "--enable-dht6=false", "--bt-enable-lpd=false",
			"--enable-peer-exchange=false", "--bt-seed-unverified=true", "--seed-ratio=0.0", $"--max-overall-upload-limit={uploadLimit ?? "0"}",
			$"--listen-port={port}", $"--dir={payloadDir}", "--console-log-level=error", torrentPath,
		};
		// The engine kills any aria2c running from its own exe path on start (orphan cleanup), so the seeder runs from a copy.
		var exe = Path.Combine(_exeDir.Path, "aria2c.exe");
		File.Copy(Path.Combine(RequiresAria2Fact.FindAria2Dir()!, "aria2c.exe"), exe);
		var psi = new ProcessStartInfo(exe) {
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (var a in args) {
			psi.ArgumentList.Add(a);
		}
		_process = Process.Start(psi)!;
		// Drain output so a full pipe can never stall the seeder.
		_process.OutputDataReceived += (_, _) => { };
		_process.ErrorDataReceived += (_, _) => { };
		_process.BeginOutputReadLine();
		_process.BeginErrorReadLine();
	}

	public void Dispose() {
		try {
			if (!_process.HasExited) {
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit(5000);
			}
		} catch (InvalidOperationException) {
			// Already gone.
		}
		_process.Dispose();
		_exeDir.Dispose();
	}
}

/// <summary>A tracker, a seeder holding the data of a generated torrent, and a leech folder: everything a download needs, all on loopback.</summary>
internal sealed class Swarm : IDisposable {
	private readonly TempDir _temp = new();
	public TestTorrent Torrent { get; }
	public LocalTracker Tracker { get; }
	public SeederProcess Seeder { get; }
	public string LeechDir { get; }
	public string Root => _temp.Path;

	public Swarm((string Path, int Size)[] files, string? seederUploadLimit = null) {
		var port = SeederProcess.FreePort();
		Tracker = new LocalTracker(port);
		Torrent = TestTorrent.Create("swarm", Tracker.AnnounceUrl, files);
		var torrentPath = Path.Combine(_temp.Path, "swarm.torrent");
		File.WriteAllBytes(torrentPath, Torrent.TorrentFile);
		var payload = Torrent.WritePayload(Path.Combine(_temp.Path, "seed"));
		Seeder = new SeederProcess(torrentPath, payload, port, seederUploadLimit);
		LeechDir = Path.Combine(_temp.Path, "leech");
		Directory.CreateDirectory(LeechDir);
	}

	public string Base64 => Convert.ToBase64String(Torrent.TorrentFile);

	public void Dispose() {
		Seeder.Dispose();
		Tracker.Dispose();
		_temp.Dispose();
	}
}
