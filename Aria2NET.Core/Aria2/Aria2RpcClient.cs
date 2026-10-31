using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Aria2NET.Core.Downloads;

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
	];
	private const int MaxListed = 1000;

	private readonly HttpClient _http = http;
	private readonly Uri _endpoint = endpoint;
	private readonly string _secret = secret;
	private int _nextId;

	public async Task<Aria2Version> GetVersionAsync(CancellationToken ct = default) {
		using var result = await CallAsync("aria2.getVersion", ct);
		var root = result.RootElement;
		var features = new List<string>();
		if (root.TryGetProperty("enabledFeatures", out var f) && f.ValueKind == JsonValueKind.Array) {
			foreach (var item in f.EnumerateArray()) {
				features.Add(item.GetString() ?? "");
			}
		}
		return new Aria2Version(root.GetProperty("version").GetString() ?? "", features);
	}

	public async Task SaveSessionAsync(CancellationToken ct = default) {
		using var _ = await CallAsync("aria2.saveSession", ct);
	}

	public async Task ShutdownAsync(CancellationToken ct = default) {
		using var _ = await CallAsync("aria2.shutdown", ct);
	}

	/// <summary>Adds one download (the URIs are treated as mirrors of the same file). Returns its GID.</summary>
	public async Task<string> AddUriAsync(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string>? options = null, CancellationToken ct = default) {
		using var result = await CallAsync("aria2.addUri", ct, uris, options ?? new Dictionary<string, string>());
		return result.RootElement.GetString() ?? throw new Aria2RpcException(0, "addUri returned no GID");
	}

	public async Task<DownloadItem> TellStatusAsync(string gid, CancellationToken ct = default) {
		using var result = await CallAsync("aria2.tellStatus", ct, gid, StatusKeys);
		return DownloadItem.FromJson(result.RootElement);
	}

	public async Task<GlobalStats> GetGlobalStatAsync(CancellationToken ct = default) {
		using var result = await CallAsync("aria2.getGlobalStat", ct);
		return GlobalStats.FromJson(result.RootElement);
	}

	/// <summary>Every field aria2 knows about one download (for the Properties dialog). Caller disposes.</summary>
	public Task<JsonDocument> TellStatusFullAsync(string gid, CancellationToken ct = default) =>
		CallAsync("aria2.tellStatus", ct, gid);

	/// <summary>Per-download options as aria2 reports them (all values are strings).</summary>
	public async Task<IReadOnlyDictionary<string, string>> GetOptionAsync(string gid, CancellationToken ct = default) {
		using var result = await CallAsync("aria2.getOption", ct, gid);
		return ReadStringMap(result.RootElement);
	}

	public async Task<IReadOnlyDictionary<string, string>> GetGlobalOptionAsync(CancellationToken ct = default) {
		using var result = await CallAsync("aria2.getGlobalOption", ct);
		return ReadStringMap(result.RootElement);
	}

	public async Task ChangeOptionAsync(string gid, IReadOnlyDictionary<string, string> options, CancellationToken ct = default) {
		using var _ = await CallAsync("aria2.changeOption", ct, gid, options);
	}

	public async Task ChangeGlobalOptionAsync(IReadOnlyDictionary<string, string> options, CancellationToken ct = default) {
		using var _ = await CallAsync("aria2.changeGlobalOption", ct, options);
	}

	/// <summary>Moves a waiting/paused download in the queue. <paramref name="how"/> is POS_SET, POS_CUR or POS_END.</summary>
	public async Task<int> ChangePositionAsync(string gid, int position, string how, CancellationToken ct = default) {
		using var result = await CallAsync("aria2.changePosition", ct, gid, position, how);
		return result.RootElement.GetInt32();
	}

	public async Task PauseAllAsync(CancellationToken ct = default) {
		using var _ = await CallAsync("aria2.forcePauseAll", ct);
	}

	public async Task UnpauseAllAsync(CancellationToken ct = default) {
		using var _ = await CallAsync("aria2.unpauseAll", ct);
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
		var list = gids.ToList();
		if (list.Count == 0) {
			return [];
		}
		using var result = await SendAsync("system.multicall", ct, w => {
			w.WriteStartArray();
			foreach (var gid in list) {
				WriteMulticallEntry(w, method, gid);
			}
			w.WriteEndArray();
		});
		var failures = new List<string>();
		foreach (var entry in result.RootElement.EnumerateArray()) {
			if (entry.ValueKind == JsonValueKind.Object) {
				failures.Add(entry.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "Unknown error");
			}
		}
		return failures;
	}

	private static Dictionary<string, string> ReadStringMap(JsonElement e) {
		var map = new Dictionary<string, string>();
		if (e.ValueKind == JsonValueKind.Object) {
			foreach (var p in e.EnumerateObject()) {
				if (p.Value.ValueKind == JsonValueKind.String) {
					map[p.Name] = p.Value.GetString() ?? "";
				}
			}
		}
		return map;
	}

	/// <summary>Active, waiting and stopped downloads plus global stats in a single HTTP request.</summary>
	public async Task<PollSnapshot> PollAsync(CancellationToken ct = default) {
		using var result = await SendAsync("system.multicall", ct, w => {
			w.WriteStartArray();
			WriteMulticallEntry(w, "aria2.tellActive", (object)StatusKeys); // cast: a bare string[] would bind to the params array itself
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
		var parts = multicallResult.EnumerateArray().Select(UnwrapMulticallEntry).ToArray();
		var items = new List<DownloadItem>();
		foreach (var part in parts.Take(3)) {
			foreach (var element in part.EnumerateArray()) {
				items.Add(DownloadItem.FromJson(element));
			}
		}
		return new PollSnapshot(items, GlobalStats.FromJson(parts[3]));
	}

	// Each multicall entry is [result] on success or {code, message} on failure.
	private static JsonElement UnwrapMulticallEntry(JsonElement entry) {
		if (entry.ValueKind == JsonValueKind.Object) {
			var code = entry.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
			var message = entry.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
			throw new Aria2RpcException(code, message);
		}
		return entry[0];
	}

	/// <summary>Calls a method with the secret token prepended and returns a document wrapping <c>result</c>. Caller disposes.</summary>
	public Task<JsonDocument> CallAsync(string method, CancellationToken ct = default, params object?[] args) =>
		SendAsync(method, ct, w => {
			w.WriteStringValue("token:" + _secret);
			foreach (var arg in args) {
				WriteValue(w, arg);
			}
		});

	/// <param name="writeParams">Writes the values inside the request's <c>params</c> array.</param>
	private async Task<JsonDocument> SendAsync(string method, CancellationToken ct, Action<Utf8JsonWriter> writeParams) {
		var body = BuildRequest(method, Interlocked.Increment(ref _nextId), writeParams);
		using var content = new ByteArrayContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		using var response = await _http.PostAsync(_endpoint, content, ct);
		var bytes = await response.Content.ReadAsByteArrayAsync(ct);

		// aria2 reports RPC errors with a non-2xx status plus a JSON error body, so parse before checking status.
		JsonDocument doc;
		try {
			doc = JsonDocument.Parse(bytes);
		}
		catch (JsonException) when (!response.IsSuccessStatusCode) {
			throw new HttpRequestException($"aria2 RPC returned HTTP {(int)response.StatusCode}");
		}

		if (doc.RootElement.TryGetProperty("error", out var error)) {
			var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
			var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
			doc.Dispose();
			throw new Aria2RpcException(code, message);
		}
		if (!doc.RootElement.TryGetProperty("result", out var result)) {
			doc.Dispose();
			throw new Aria2RpcException(0, "RPC response had neither result nor error");
		}

		var clone = JsonDocument.Parse(result.GetRawText());
		doc.Dispose();
		return clone;
	}

	private static byte[] BuildRequest(string method, int id, Action<Utf8JsonWriter> writeParams) {
		using var stream = new MemoryStream();
		using (var w = new Utf8JsonWriter(stream)) {
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
		foreach (var arg in args) {
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
				foreach (var (k, v) in map) {
					w.WriteString(k, v);
				}
				w.WriteEndObject();
				break;
			case IEnumerable<string> list:
				w.WriteStartArray();
				foreach (var item in list) {
					w.WriteStringValue(item);
				}
				w.WriteEndArray();
				break;
			default:
				throw new ArgumentException($"Unsupported RPC argument type {value.GetType()}");
		}
	}
}
