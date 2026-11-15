using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;

namespace Aria2NET.Core.Aria2;

public sealed record Aria2Version(string Version, IReadOnlyList<string> EnabledFeatures);

public sealed class Aria2RpcException(int code, string message) : Exception(message) {
	public int Code { get; } = code;
}

/// <summary>aria2 JSON-RPC client (HTTP).</summary>
public sealed class Aria2RpcClient(HttpClient http, Uri endpoint, string secret) {
	/// <summary>Only the fields we show; keeps each poll response small.</summary>
	private static readonly string[] StatusKeys = [
		"gid", "status", "totalLength", "completedLength", "downloadSpeed", "files", "dir", "errorCode", "errorMessage", "bittorrent",
		"infoHash", "seeder", "uploadSpeed", "uploadLength", "numSeeders", "connections", "followedBy", "belongsTo",
	];
	private const int MaxListed = 1000;

	private readonly HttpClient _http = http;
	private readonly Uri _endpoint = endpoint;
	private readonly string _secret = secret;
	private int _nextId;

	public async Task<Aria2Version> GetVersionAsync(CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.getVersion", ct);
		JsonElement root = result.RootElement;
		List<string> features = [];
		if (root.TryGetProperty("enabledFeatures", out JsonElement f) && f.ValueKind == JsonValueKind.Array) {
			foreach (JsonElement item in f.EnumerateArray()) {
				features.Add(item.GetString() ?? "");
			}
		}
		return new Aria2Version(root.GetProperty("version").GetString() ?? "", features);
	}

	public async Task SaveSessionAsync(CancellationToken ct = default) {
		using JsonDocument _ = await CallAsync("aria2.saveSession", ct);
	}

	public async Task ShutdownAsync(CancellationToken ct = default) {
		using JsonDocument _ = await CallAsync("aria2.shutdown", ct);
	}

	/// <summary>Adds one download (the URIs are treated as mirrors of the same file). Returns its GID.</summary>
	public async Task<string> AddUriAsync(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.addUri", ct, uris, options ?? new Dictionary<string, string>());
		return result.RootElement.GetString() ?? throw new Aria2RpcException(0, "addUri returned no GID");
	}

	/// <summary>Adds a .torrent (its bytes, base64 in the request). Returns the GID. <paramref name="webSeeds"/> are extra HTTP sources for the same content.</summary>
	public async Task<string> AddTorrentAsync(byte[] torrent, IReadOnlyDictionary<string, string>? options = null, IReadOnlyList<string>? webSeeds = null, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.addTorrent", ct, Convert.ToBase64String(torrent), webSeeds ?? [], options ?? new Dictionary<string, string>());
		return result.RootElement.GetString() ?? throw new Aria2RpcException(0, "addTorrent returned no GID");
	}

	/// <summary>Adds a metalink document. aria2 starts one download per file in it and returns all their GIDs.</summary>
	public async Task<IReadOnlyList<string>> AddMetalinkAsync(byte[] metalink, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.addMetalink", ct, Convert.ToBase64String(metalink), options ?? new Dictionary<string, string>());
		return result.RootElement.EnumerateArray().Select(g => g.GetString() ?? "").Where(g => g.Length > 0).ToList();
	}

	/// <summary>Files of a download with their selection state and progress.</summary>
	public async Task<IReadOnlyList<DownloadFile>> GetFilesAsync(string gid, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.getFiles", ct, gid);
		return result.RootElement.EnumerateArray().Select(DownloadFile.FromJson).ToList();
	}

	/// <summary>Connected peers of a torrent (empty for anything else or when nothing is connected).</summary>	
	public async Task<IReadOnlyList<PeerInfo>> GetPeersAsync(string gid, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.getPeers", ct, gid);
		return result.RootElement.EnumerateArray().Select(PeerInfo.FromJson).ToList();
	}

	public async Task<DownloadItem> TellStatusAsync(string gid, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.tellStatus", ct, gid, StatusKeys);
		return DownloadItem.FromJson(result.RootElement);
	}

	public async Task<GlobalStats> GetGlobalStatAsync(CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.getGlobalStat", ct);
		return GlobalStats.FromJson(result.RootElement);
	}

	/// <summary>Every field aria2 knows about one download (for the Properties dialog). Caller disposes.</summary>
	public Task<JsonDocument> TellStatusFullAsync(string gid, CancellationToken ct = default) =>
		CallAsync("aria2.tellStatus", ct, gid);

	/// <summary>Per-download options as aria2 reports them (all values are strings).</summary>
	public async Task<IReadOnlyDictionary<string, string>> GetOptionAsync(string gid, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.getOption", ct, gid);
		return ReadStringMap(result.RootElement);
	}

	public async Task<IReadOnlyDictionary<string, string>> GetGlobalOptionAsync(CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.getGlobalOption", ct);
		return ReadStringMap(result.RootElement);
	}

	public async Task ChangeOptionAsync(string gid, IReadOnlyDictionary<string, string> options, CancellationToken ct = default) {
		using JsonDocument _ = await CallAsync("aria2.changeOption", ct, gid, options);
	}

	public async Task ChangeGlobalOptionAsync(IReadOnlyDictionary<string, string> options, CancellationToken ct = default) {
		using JsonDocument _ = await CallAsync("aria2.changeGlobalOption", ct, options);
	}

	/// <summary>Moves a waiting/paused download in the queue. <paramref name="how"/> is POS_SET, POS_CUR or POS_END.</summary>
	public async Task<int> ChangePositionAsync(string gid, int position, string how, CancellationToken ct = default) {
		using JsonDocument result = await CallAsync("aria2.changePosition", ct, gid, position, how);
		return result.RootElement.GetInt32();
	}

	public async Task PauseAllAsync(CancellationToken ct = default) {
		using JsonDocument _ = await CallAsync("aria2.forcePauseAll", ct);
	}

	public async Task UnpauseAllAsync(CancellationToken ct = default) {
		using JsonDocument _ = await CallAsync("aria2.unpauseAll", ct);
	}

	/// <summary>Pauses (immediately, without waiting for peers to finish) the given downloads. Returns the failures, one per GID that failed.</summary>
	public Task<IReadOnlyList<string>> PauseAsync(IEnumerable<string> gids, CancellationToken ct = default) => BatchAsync("aria2.forcePause", gids, ct);

	public Task<IReadOnlyList<string>> UnpauseAsync(IEnumerable<string> gids, CancellationToken ct = default) => BatchAsync("aria2.unpause", gids, ct);

	/// <summary>Stops and removes running/queued downloads (aria2 keeps a "removed" entry until <see cref="RemoveResultAsync"/>).</summary>
	public Task<IReadOnlyList<string>> RemoveAsync(IEnumerable<string> gids, CancellationToken ct = default) => BatchAsync("aria2.forceRemove", gids, ct);

	/// <summary>Forgets finished (complete/error/removed) downloads.</summary>
	public Task<IReadOnlyList<string>> RemoveResultAsync(IEnumerable<string> gids, CancellationToken ct = default) => BatchAsync("aria2.removeDownloadResult", gids, ct);

	/// <summary>Calls <paramref name="method"/> once per GID in a single request. Returns one message per call that failed.</summary>
	private async Task<IReadOnlyList<string>> BatchAsync(string method, IEnumerable<string> gids, CancellationToken ct) {
		List<string> list = gids.ToList();
		if (list.Count == 0) {
			return [];
		}
		using JsonDocument result = await SendAsync("system.multicall", ct, w => {
			w.WriteStartArray();
			foreach (string gid in list) {
				WriteMulticallEntry(w, method, gid);
			}
			w.WriteEndArray();
		});
		List<string> failures = new List<string>();
		foreach (JsonElement entry in result.RootElement.EnumerateArray()) {
			if (entry.ValueKind == JsonValueKind.Object) {
				failures.Add(entry.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "Unknown error");
			}
		}
		return failures;
	}

	private static Dictionary<string, string> ReadStringMap(JsonElement e) {
		Dictionary<string, string> map = new Dictionary<string, string>();
		if (e.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty p in e.EnumerateObject()) {
				if (p.Value.ValueKind == JsonValueKind.String) {
					map[p.Name] = p.Value.GetString() ?? "";
				}
			}
		}
		return map;
	}

	/// <summary>Active, waiting and stopped downloads plus global stats in a single HTTP request.</summary>
	public async Task<PollSnapshot> PollAsync(CancellationToken ct = default) {
		using JsonDocument result = await SendAsync("system.multicall", ct, w => {
			w.WriteStartArray();
			WriteMulticallEntry(w, "aria2.tellActive", (object) StatusKeys); // cast: a bare string[] would bind to the params array itself
			WriteMulticallEntry(w, "aria2.tellWaiting", 0, MaxListed, StatusKeys);
			WriteMulticallEntry(w, "aria2.tellStopped", 0, MaxListed, StatusKeys);
			WriteMulticallEntry(w, "aria2.getGlobalStat");
			w.WriteEndArray();
		});
		return ParseSnapshot(result.RootElement);
	}

	internal static PollSnapshot ParseSnapshot(JsonElement multicallResult) {
		if (multicallResult.ValueKind != JsonValueKind.Array || multicallResult.GetArrayLength() != 4) {
			throw new Aria2RpcException(0, "Unexpected multicall response shape");
		}
		JsonElement[] parts = multicallResult.EnumerateArray().Select(UnwrapMulticallEntry).ToArray();
		List<DownloadItem> items = new List<DownloadItem>();
		foreach (JsonElement part in parts.Take(3)) {
			foreach (JsonElement element in part.EnumerateArray()) {
				items.Add(DownloadItem.FromJson(element));
			}
		}
		return new PollSnapshot(items, GlobalStats.FromJson(parts[3]));
	}

	// Each multicall entry is [result] on success or {code, message} on failure.
	private static JsonElement UnwrapMulticallEntry(JsonElement entry) {
		if (entry.ValueKind == JsonValueKind.Object) {
			int code = entry.TryGetProperty("code", out JsonElement c) ? c.GetInt32() : 0;
			string message = entry.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
			throw new Aria2RpcException(code, message);
		}
		return entry[0];
	}

	/// <summary>Calls a method with the secret token prepended and returns a document wrapping <c>result</c>. Caller disposes.</summary>
	public Task<JsonDocument> CallAsync(string method, CancellationToken ct = default, params object?[] args) =>
		SendAsync(method, ct, w => {
			w.WriteStringValue("token:" + _secret);
			foreach (object? arg in args) {
				WriteValue(w, arg);
			}
		});

	/// <param name="writeParams">Writes the values inside the request's <c>params</c> array.</param>
	private async Task<JsonDocument> SendAsync(string method, CancellationToken ct, Action<Utf8JsonWriter> writeParams) {
		byte[] body = BuildRequest(method, Interlocked.Increment(ref _nextId), writeParams);
		using ByteArrayContent content = new ByteArrayContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		using HttpResponseMessage response = await _http.PostAsync(_endpoint, content, ct);
		byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);

		// aria2 reports RPC errors with a non-2xx status plus a JSON error body, so parse before checking status.
		JsonDocument doc;
		try {
			doc = JsonDocument.Parse(bytes);
		} catch (JsonException) when (!response.IsSuccessStatusCode) {
			throw new HttpRequestException($"aria2 RPC returned HTTP {(int) response.StatusCode}");
		}

		if (doc.RootElement.TryGetProperty("error", out JsonElement error)) {
			int code = error.TryGetProperty("code", out JsonElement c) ? c.GetInt32() : 0;
			string message = error.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
			doc.Dispose();
			throw new Aria2RpcException(code, message);
		}
		if (!doc.RootElement.TryGetProperty("result", out JsonElement result)) {
			doc.Dispose();
			throw new Aria2RpcException(0, "RPC response had neither result nor error");
		}

		JsonDocument clone = JsonDocument.Parse(result.GetRawText());
		doc.Dispose();
		return clone;
	}

	private static byte[] BuildRequest(string method, int id, Action<Utf8JsonWriter> writeParams) {
		using MemoryStream stream = new MemoryStream();
		using (Utf8JsonWriter w = new Utf8JsonWriter(stream)) {
			w.WriteStartObject();
			w.WriteString("jsonrpc", "2.0");
			w.WriteString("id", id.ToString(CultureInfo.InvariantCulture));
			w.WriteString("method", method);
			w.WritePropertyName("params");
			w.WriteStartArray();
			writeParams(w);
			w.WriteEndArray();
			w.WriteEndObject();
		}
		return stream.ToArray();
	}

	private void WriteMulticallEntry(Utf8JsonWriter w, string method, params object?[] args) {
		w.WriteStartObject();
		w.WriteString("methodName", method);
		w.WriteStartArray("params");
		w.WriteStringValue("token:" + _secret);
		foreach (object? arg in args) {
			WriteValue(w, arg);
		}
		w.WriteEndArray();
		w.WriteEndObject();
	}

	private static void WriteValue(Utf8JsonWriter w, object? value) {
		switch (value) {
			case null:
				w.WriteNullValue();
				break;
			case string s:
				w.WriteStringValue(s);
				break;
			case int i:
				w.WriteNumberValue(i);
				break;
			case long l:
				w.WriteNumberValue(l);
				break;
			case IReadOnlyDictionary<string, string> map:
				w.WriteStartObject();
				foreach ((string? k, string? v) in map) {
					if (k == "header") {
						// aria2 takes repeatable options as an array; one header per line in the value.
						w.WriteStartArray(k);
						foreach (string line in v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
							w.WriteStringValue(line);
						}
						w.WriteEndArray();
					} else {
						w.WriteString(k, v);
					}
				}
				w.WriteEndObject();
				break;
			case IEnumerable<string> list:
				w.WriteStartArray();
				foreach (string item in list) {
					w.WriteStringValue(item);
				}
				w.WriteEndArray();
				break;
			default:
				throw new ArgumentException($"Unsupported RPC argument type {value.GetType()}");
		}
	}
}
