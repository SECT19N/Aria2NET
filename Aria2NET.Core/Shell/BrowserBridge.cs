using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Shell;

/// <summary>One download handed over by the browser extension. <see cref="ToString"/> hides everything: cookies must never reach a log.</summary>
public sealed record BrowserDownloadRequest(string Url, string? FileName = null, string? Referrer = null, string? Cookies = null, string? UserAgent = null) {
	public AddOptions ToAddOptions() => new(Referer: Referrer, Cookie: Cookies, UserAgent: UserAgent);

	private bool PrintMembers(StringBuilder builder) {
		builder.Append("(hidden)");
		return true;
	}
}

/// <summary>What the extension can say: "are you there?" (<see cref="Download"/> is null) or "take this download".</summary>
public sealed record BrowserMessage(BrowserDownloadRequest? Download) {
	public bool IsPing => Download is null;
}

/// <summary>
/// The wire between the browser extension and the app. The browser starts Aria2NET.exe as a "native messaging host" and talks to it over
/// stdin/stdout in frames (a 4-byte little-endian length, then UTF-8 JSON); that short-lived process passes the frame on to the running app
/// over a named pipe only this Windows user can open. Everything that arrives is treated as hostile until <see cref="Parse"/> accepts it.
/// </summary>
public static class BrowserBridge {
	/// <summary>The native messaging host name the extension asks the browser for.</summary>
	public const string HostName = "io.github.sect19n.aria2net";

	/// <summary>Larger frames are refused; a request with a long cookie header is a few KB.</summary>
	public const int MaxMessageBytes = 256 * 1024;

	private const int MaxUrlLength = 8192;
	private const int MaxCookieLength = 64 * 1024;
	private const int MaxUserAgentLength = 512;
	private const int MaxFileNameLength = 200;

	/// <summary>
	/// The extensions allowed to talk to the app. The first is the ID that the public key in extension/manifest.json gives a copy loaded
	/// unpacked; a store listing gets its own ID, which has to be added here when the extension is published.
	/// </summary>
	public static IReadOnlyList<string> ExtensionIds { get; } = ["iegmhkihhkgebgdiopmpajafmkdjepne"];

	public static IEnumerable<string> AllowedOrigins => ExtensionIds.Select(id => $"chrome-extension://{id}/");

	/// <summary>The pipe between the host process and the app. Named per user so two people signed in to one PC do not meet.</summary>
	public static string PipeName { get; } = "Aria2NET.Browser." + new string([.. Environment.UserName.Where(char.IsLetterOrDigit)]);

	/// <summary>True when the browser started this process as its native messaging host (it passes the calling extension's origin as an argument).</summary>
	public static bool IsHostLaunch(IEnumerable<string> args) => args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));

	/// <summary>True when that origin is one of ours. The browser enforces this too (the host manifest lists the origins); this is the second lock.</summary>
	public static bool IsAllowedHostLaunch(IEnumerable<string> args) => args.Any(a => AllowedOrigins.Contains(a.Trim(), StringComparer.Ordinal));

	// ---------------------------------------------------------------- messages

	/// <summary>Reads a message from the extension. Returns an error text instead when it is not one this app should act on.</summary>
	public static (BrowserMessage? Message, string? Error) Parse(string json) {
		if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes) {
			return (null, "The message is too large.");
		}
		JsonDocument document;
		try {
			document = JsonDocument.Parse(json);
		} catch (JsonException) {
			return (null, "The message is not valid JSON.");
		}
		using (document) {
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				return (null, "The message is not an object.");
			}
			switch (Text(root, "type")) {
				case "ping":
					return (new BrowserMessage(null), null);
				case "download":
					break;
				default:
					return (null, "Unknown message type.");
			}

			var url = Text(root, "url")?.Trim();
			if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength || url.Any(char.IsControl)
				|| !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "sftp")) {
				return (null, "Only http, https, ftp and sftp links can be downloaded.");
			}
			return (new BrowserMessage(new BrowserDownloadRequest(
				url,
				SafeFileName(Text(root, "filename")),
				SafeReferrer(Text(root, "referrer")),
				SafeHeaderValue(Text(root, "cookies"), MaxCookieLength),
				SafeHeaderValue(Text(root, "userAgent"), MaxUserAgentLength))), null);
		}
	}

	/// <summary>The answer sent back to the extension.</summary>
	public static string Reply(bool ok, string? error = null) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteBoolean("ok", ok);
			if (!string.IsNullOrEmpty(error)) {
				writer.WriteString("error", error);
			}
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static string? Text(JsonElement e, string name) =>
		e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	/// <summary>A bare file name or nothing: the browser's suggestion must not be able to choose a folder.</summary>
	internal static string? SafeFileName(string? name) {
		if (string.IsNullOrWhiteSpace(name)) {
			return null;
		}
		var bare = name.Replace('\\', '/');
		bare = bare[(bare.LastIndexOf('/') + 1)..].Trim().TrimEnd('.');
		return bare.Length is 0 or > MaxFileNameLength || bare is "." or ".." || bare.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || bare.Any(char.IsControl) ? null : bare;
	}

	private static string? SafeReferrer(string? referrer) =>
		!string.IsNullOrWhiteSpace(referrer) && !referrer.Any(char.IsControl) && referrer.Length <= MaxUrlLength
			&& Uri.TryCreate(referrer.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? referrer.Trim() : null;

	/// <summary>A newline here could start another header, so a value with control characters is dropped whole.</summary>
	private static string? SafeHeaderValue(string? value, int maxLength) =>
		string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl) ? null : value.Trim();

	// ---------------------------------------------------------------- frames

	/// <summary>Reads one frame. Null when the other side closed before sending one.</summary>
	/// <exception cref="InvalidDataException">The frame is larger than <see cref="MaxMessageBytes"/>, cut short, or not UTF-8.</exception>
	public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct = default) {
		var header = new byte[4];
		var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct);
		if (read == 0) {
			return null;
		}
		if (read < 4) {
			throw new InvalidDataException("The frame header is incomplete.");
		}
		var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
		if (length > MaxMessageBytes) {
			throw new InvalidDataException("The frame is too large.");
		}
		var body = new byte[length];
		if (await stream.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, ct) < body.Length) {
			throw new InvalidDataException("The frame is incomplete.");
		}
		try {
			return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
		} catch (ArgumentException) {
			throw new InvalidDataException("The frame is not UTF-8 text.");
		}
	}

	public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct = default) {
		var body = Encoding.UTF8.GetBytes(json);
		var frame = new byte[4 + body.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint) body.Length);
		body.CopyTo(frame, 4);
		await stream.WriteAsync(frame, ct);
		await stream.FlushAsync(ct);
	}

	// ---------------------------------------------------------------- host side

	/// <summary>Sends one frame to the running app and returns its answer. Null when the app is not listening (not running, or catching is off).</summary>
	public static async Task<string?> SendToAppAsync(string json, TimeSpan connectTimeout, string? pipeName = null, CancellationToken ct = default) {
		await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try {
			await pipe.ConnectAsync(connectTimeout, ct);
		} catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) {
			return null;
		}
		try {
			await WriteFrameAsync(pipe, json, ct);
			return await ReadFrameAsync(pipe, ct);
		} catch (Exception ex) when (ex is IOException or InvalidDataException) {
			return null;
		}
	}
}

/// <summary>The app's end of the pipe: takes one message at a time from the host process and answers it.</summary>
public sealed class BrowserBridgeServer(Func<BrowserMessage, Task<string?>> handle, ILogger logger, string? pipeName = null) {
	private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);
	private readonly string _pipeName = pipeName ?? BrowserBridge.PipeName;
	private CancellationTokenSource? _cts;
	private Task? _loop;

	public bool IsRunning => _cts is not null;

	public void Start() {
		if (_cts is not null) {
			return;
		}
		_cts = new CancellationTokenSource();
		_loop = Task.Run(() => RunAsync(_cts.Token));
	}

	public async Task StopAsync() {
		if (_cts is null) {
			return;
		}
		await _cts.CancelAsync();
		try {
			await _loop!;
		} catch (OperationCanceledException) {
			// Expected.
		}
		_cts.Dispose();
		_cts = null;
		_loop = null;
	}

	private async Task RunAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
					PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
				await pipe.WaitForConnectionAsync(ct);
				// A client that connects and then says nothing must not block the next one for ever.
				using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
				exchange.CancelAfter(ExchangeTimeout);
				await AnswerAsync(pipe, exchange.Token);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				return;
			} catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException) {
				logger.LogDebug(ex, "A browser bridge connection failed");
				// Not being able to create the pipe at all would otherwise spin.
				await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ContinueWith(_ => { }, TaskScheduler.Default);
			}
		}
	}

	private async Task AnswerAsync(Stream pipe, CancellationToken ct) {
		if (await BrowserBridge.ReadFrameAsync(pipe, ct) is not { } json) {
			return;
		}
		var (message, error) = BrowserBridge.Parse(json);
		if (message is not null) {
			try {
				error = await handle(message);
			} catch (Exception ex) {
				logger.LogWarning(ex, "Handling a browser download failed");
				error = "Aria2NET could not take the download.";
			}
		}
		await BrowserBridge.WriteFrameAsync(pipe, BrowserBridge.Reply(error is null, error), ct);
	}
}
