using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Aria2NET.Tests;

public class BrowserBridgeMessageTests {
	[Fact]
	public void Reads_a_download_with_everything_the_extension_sends() {
		(BrowserMessage? message, string? error) = BrowserBridge.Parse("""{"type":"download","url":"https://example.com/a.zip","filename":"a.zip","referrer":"https://example.com/page","cookies":"sid=1; theme=dark","userAgent":"Mozilla/5.0"}""");

		Assert.Null(error);
		BrowserDownloadRequest request = message!.Download!;
		Assert.Equal("https://example.com/a.zip", request.Url);
		Assert.Equal("a.zip", request.FileName);
		AddOptions options = request.ToAddOptions();
		Assert.Null(options.Validate());
		IReadOnlyDictionary<string, string> aria2 = options.ToOptions();
		Assert.Equal("https://example.com/page", aria2["referer"]);
		Assert.Equal("Cookie: sid=1; theme=dark", aria2["header"]);
		Assert.Equal("Mozilla/5.0", aria2["user-agent"]);
	}

	[Fact]
	public void A_ping_carries_no_download() {
		(BrowserMessage? message, string? error) = BrowserBridge.Parse("""{"type":"ping"}""");

		Assert.Null(error);
		Assert.True(message!.IsPing);
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("""{"type":"run","url":"https://example.com/a"}""")]
	[InlineData("""{"type":"download"}""")]
	[InlineData("""{"type":"download","url":"file:///C:/Windows/win.ini"}""")]
	[InlineData("""{"type":"download","url":"javascript:alert(1)"}""")]
	[InlineData("""{"type":"download","url":"magnet:?xt=urn:btih:abc"}""")]
	[InlineData("""{"type":"download","url":"example.com/a.zip"}""")]
	[InlineData("""{"type":"download","url":"https://example.com/a\nb"}""")]
	public void Refuses_what_it_should_not_act_on(string json) {
		(BrowserMessage? message, string? error) = BrowserBridge.Parse(json);

		Assert.Null(message);
		Assert.False(string.IsNullOrEmpty(error));
	}

	[Theory]
	[InlineData(@"C:\\Users\\me\\Downloads\\a.zip", "a.zip")]
	[InlineData("../../evil.exe", "evil.exe")]
	[InlineData("report.pdf", "report.pdf")]
	[InlineData("a:b.txt", null)]
	[InlineData("..", null)]
	[InlineData("   ", null)]
	public void Keeps_only_a_bare_file_name(string given, string? expected) {
		(BrowserMessage? message, string? _) = BrowserBridge.Parse($$"""{"type":"download","url":"https://example.com/x","filename":"{{given}}"}""");

		Assert.Equal(expected, message!.Download!.FileName);
	}

	[Fact]
	public void Drops_header_values_that_could_smuggle_another_header() {
		(BrowserMessage? message, string? _) = BrowserBridge.Parse("""{"type":"download","url":"https://example.com/x","referrer":"ftp://example.com/","cookies":"a=1\r\nX-Evil: 1","userAgent":"UA\nX-Evil: 1"}""");

		BrowserDownloadRequest request = message!.Download!;
		Assert.Null(request.Referrer);
		Assert.Null(request.Cookies);
		Assert.Null(request.UserAgent);
	}

	[Fact]
	public void Refuses_a_message_over_the_size_limit() {
		string json = $$"""{"type":"download","url":"https://example.com/x","cookies":"{{new string('a', BrowserBridge.MaxMessageBytes)}}"}""";

		Assert.Null(BrowserBridge.Parse(json).Message);
	}

	[Fact]
	public void Never_prints_the_cookies() {
		string text = new BrowserDownloadRequest("https://example.com/x", Cookies: "secret=1").ToString();

		Assert.DoesNotContain("secret", text);
	}

	[Fact]
	public void The_reply_says_ok_or_why_not() {
		Assert.True(JsonDocument.Parse(BrowserBridge.Reply(true)).RootElement.GetProperty("ok").GetBoolean());
		JsonElement failed = JsonDocument.Parse(BrowserBridge.Reply(false, "no \"luck\"")).RootElement;
		Assert.False(failed.GetProperty("ok").GetBoolean());
		Assert.Equal("no \"luck\"", failed.GetProperty("error").GetString());
	}

	[Fact]
	public void Only_our_extension_counts_as_a_host_launch() {
		Assert.True(BrowserBridge.IsHostLaunch(["chrome-extension://someoneelse/", "--parent-window=0"]));
		Assert.False(BrowserBridge.IsAllowedHostLaunch(["chrome-extension://someoneelse/", "--parent-window=0"]));
		Assert.True(BrowserBridge.IsAllowedHostLaunch([$"chrome-extension://{BrowserBridge.ExtensionIds[0]}/", "--parent-window=0"]));
		Assert.False(BrowserBridge.IsHostLaunch(["--autostart", @"C:\a.torrent"]));
	}
}

public class BrowserBridgeFrameTests {
	[Fact]
	public async Task A_frame_survives_the_round_trip() {
		using MemoryStream stream = new MemoryStream();
		await BrowserBridge.WriteFrameAsync(stream, """{"type":"ping","note":"žluťoučký"}""");
		stream.Position = 0;

		Assert.Equal(4 + Encoding.UTF8.GetByteCount("""{"type":"ping","note":"žluťoučký"}"""), stream.Length);
		Assert.Equal("""{"type":"ping","note":"žluťoučký"}""", await BrowserBridge.ReadFrameAsync(stream));
		Assert.Null(await BrowserBridge.ReadFrameAsync(stream)); // nothing more
	}

	[Fact]
	public async Task Refuses_an_oversized_or_cut_off_frame() {
		MemoryStream huge = new MemoryStream(BitConverter.GetBytes((uint) BrowserBridge.MaxMessageBytes + 1));
		await Assert.ThrowsAsync<InvalidDataException>(() => BrowserBridge.ReadFrameAsync(huge));

		MemoryStream cut = new MemoryStream([.. BitConverter.GetBytes(10u), (byte) '{']);
		await Assert.ThrowsAsync<InvalidDataException>(() => BrowserBridge.ReadFrameAsync(cut));
	}
}

public class BrowserBridgeServerTests {
	private static string NewPipe() => "a2n-test-" + Guid.NewGuid().ToString("N");

	[Fact]
	public async Task The_app_answers_what_the_host_passes_on() {
		string pipe = NewPipe();
		List<BrowserMessage> seen = new List<BrowserMessage>();
		BrowserBridgeServer server = new BrowserBridgeServer(m => {
			seen.Add(m);
			return Task.FromResult<string?>(m.Download?.Url.EndsWith("bad") == true ? "No room." : null);
		}, NullLogger.Instance, pipe);
		server.Start();
		try {
			string? good = await BrowserBridge.SendToAppAsync("""{"type":"download","url":"https://example.com/good"}""", TimeSpan.FromSeconds(10), pipe);
			string? bad = await BrowserBridge.SendToAppAsync("""{"type":"download","url":"https://example.com/bad"}""", TimeSpan.FromSeconds(10), pipe);
			string? refused = await BrowserBridge.SendToAppAsync("""{"type":"download","url":"file:///C:/x"}""", TimeSpan.FromSeconds(10), pipe);

			Assert.True(JsonDocument.Parse(good!).RootElement.GetProperty("ok").GetBoolean());
			Assert.Equal("No room.", JsonDocument.Parse(bad!).RootElement.GetProperty("error").GetString());
			Assert.False(JsonDocument.Parse(refused!).RootElement.GetProperty("ok").GetBoolean());
			Assert.Equal(2, seen.Count); // the refused one never reached the handler
		} finally {
			await server.StopAsync();
		}
	}

	[Fact]
	public async Task Nobody_listening_is_reported_as_no_answer() {
		Assert.Null(await BrowserBridge.SendToAppAsync("""{"type":"ping"}""", TimeSpan.FromMilliseconds(200), NewPipe()));
	}
}

[SupportedOSPlatform("windows")]
public sealed class NativeMessagingRegistrationTests : IDisposable {
	private readonly string _path = @"Software\Aria2NET.Tests\" + Guid.NewGuid().ToString("N");
	private readonly RegistryKey _software;
	private readonly TempDir _dir = new();

	public NativeMessagingRegistrationTests() {
		_software = Registry.CurrentUser.CreateSubKey(_path, writable: true);
	}

	public void Dispose() {
		_software.Dispose();
		Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
		_dir.Dispose();
	}

	private NativeMessagingRegistration Create(string exe = @"C:\Apps\Aria2NET\Aria2NET.exe") => new(exe, _dir.Path, _software);

	[Fact]
	public void Registers_a_manifest_for_chrome_and_edge() {
		NativeMessagingRegistration registration = Create();
		Assert.False(registration.IsRegistered());

		registration.Register();

		Assert.True(registration.IsRegistered());
		JsonElement manifest = JsonDocument.Parse(File.ReadAllText(registration.ManifestPath)).RootElement;
		Assert.Equal(BrowserBridge.HostName, manifest.GetProperty("name").GetString());
		Assert.Equal(@"C:\Apps\Aria2NET\Aria2NET.exe", manifest.GetProperty("path").GetString());
		Assert.Equal("stdio", manifest.GetProperty("type").GetString());
		Assert.Equal([.. BrowserBridge.AllowedOrigins], manifest.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()!).ToArray());
		foreach (string? browser in new[] { @"Google\Chrome", @"Microsoft\Edge" }) {
			using RegistryKey? key = _software.OpenSubKey($@"{browser}\NativeMessagingHosts\{BrowserBridge.HostName}");
			Assert.Equal(registration.ManifestPath, key!.GetValue(null));
		}
	}

	[Fact]
	public void Notices_when_the_app_has_moved_and_repairs_it() {
		Create().Register();
		NativeMessagingRegistration moved = Create(@"D:\Elsewhere\Aria2NET.exe");

		Assert.False(moved.IsRegistered());
		moved.Register();

		Assert.True(moved.IsRegistered());
	}

	[Fact]
	public void Unregister_removes_everything_and_can_be_repeated() {
		NativeMessagingRegistration registration = Create();
		registration.Register();

		registration.Unregister();
		registration.Unregister();

		Assert.False(File.Exists(registration.ManifestPath));
		Assert.Null(_software.OpenSubKey($@"Google\Chrome\NativeMessagingHosts\{BrowserBridge.HostName}"));
		Assert.Null(_software.OpenSubKey($@"Microsoft\Edge\NativeMessagingHosts\{BrowserBridge.HostName}"));
	}
}
