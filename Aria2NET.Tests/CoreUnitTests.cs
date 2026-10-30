using System.Net;
using System.Text;
using System.Text.Json;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Settings;

namespace Aria2NET.Tests;

public class AppPathsTests {
	[Fact]
	public void Derives_paths_from_roots() {
		var p = new AppPaths(@"C:\data", @"C:\app\tools\aria2");
		Assert.Equal(@"C:\data\logs", p.LogsDir);
		Assert.Equal(@"C:\data\settings.json", p.SettingsFile);
		Assert.Equal(@"C:\app\tools\aria2\aria2c.exe", p.Aria2Exe);
	}

	[Fact]
	public void EnsureDirectories_creates_folders() {
		using var t = new TempDir();
		var p = new AppPaths(Path.Combine(t.Path, "data"), t.Path);
		p.EnsureDirectories();
		Assert.True(Directory.Exists(p.LogsDir));
		Assert.True(Directory.Exists(p.EngineDir));
	}
}

public class SettingsStoreTests {
	private static SettingsStore<AppSettings> Create(string path) => new(path, AppSettingsJsonContext.Default.AppSettings);

	[Fact]
	public void Missing_file_gives_defaults() {
		using var t = new TempDir();
		Assert.Equal(1, Create(Path.Combine(t.Path, "s.json")).Load().SchemaVersion);
	}

	[Fact]
	public void Round_trips_and_leaves_no_temp_file() {
		using var t = new TempDir();
		var path = Path.Combine(t.Path, "sub", "s.json");
		var store = Create(path);
		store.Save(new AppSettings { SchemaVersion = 7 });
		Assert.Equal(7, store.Load().SchemaVersion);
		Assert.False(File.Exists(path + ".tmp"));
	}

	[Fact]
	public void Corrupt_file_is_backed_up_and_defaults_returned() {
		using var t = new TempDir();
		var path = Path.Combine(t.Path, "s.json");
		File.WriteAllText(path, "{ not json");
		var loaded = Create(path).Load();
		Assert.Equal(1, loaded.SchemaVersion);
		Assert.False(File.Exists(path));
		Assert.Single(Directory.GetFiles(t.Path, "s.json.corrupt-*.bak"));
	}
}

public class Aria2ConfigTests {
	[Fact]
	public void Random_config_uses_loopback_port_and_unique_secrets() {
		var a = Aria2Config.CreateRandom(@"C:\dl", 1);
		var b = Aria2Config.CreateRandom(@"C:\dl", 1);
		Assert.InRange(a.Port, 1024, 65535);
		Assert.Equal(64, a.Secret.Length);
		Assert.NotEqual(a.Secret, b.Secret);
	}

	[Fact]
	public void Render_contains_security_critical_options() {
		var paths = new AppPaths(@"C:\data", @"C:\tools");
		var text = new Aria2Config(6800, "abc", 42, @"C:\dl").Render(paths);
		Assert.Contains("rpc-listen-all=false", text);
		Assert.Contains("rpc-allow-origin-all=false", text);
		Assert.Contains("rpc-secret=abc", text);
		Assert.Contains("stop-with-process=42", text);
		Assert.Contains("rpc-listen-port=6800", text);
	}
}

public class Aria2RpcClientTests {
	private static Aria2RpcClient Client(FakeHandler handler) =>
		new(new HttpClient(handler), new Uri("http://127.0.0.1:1/jsonrpc"), "s3cret");

	private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
		new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Fact]
	public async Task GetVersion_sends_token_and_parses_result() {
		var handler = new FakeHandler((_, _) => Json("""{"id":"1","jsonrpc":"2.0","result":{"version":"1.37.0","enabledFeatures":["BitTorrent","HTTPS"]}}"""));
		var v = await Client(handler).GetVersionAsync();
		Assert.Equal("1.37.0", v.Version);
		Assert.Equal(["BitTorrent", "HTTPS"], v.EnabledFeatures);

		using var sent = JsonDocument.Parse(handler.Bodies.Single());
		Assert.Equal("aria2.getVersion", sent.RootElement.GetProperty("method").GetString());
		Assert.Equal("token:s3cret", sent.RootElement.GetProperty("params")[0].GetString());
	}

	[Fact]
	public async Task Error_response_throws_with_code_even_on_http_400() {
		var handler = new FakeHandler((_, _) => Json("""{"id":"1","jsonrpc":"2.0","error":{"code":1,"message":"Unauthorized"}}""", HttpStatusCode.BadRequest));
		var ex = await Assert.ThrowsAsync<Aria2RpcException>(() => Client(handler).GetVersionAsync());
		Assert.Equal(1, ex.Code);
		Assert.Equal("Unauthorized", ex.Message);
	}

	[Fact]
	public async Task Non_json_http_failure_throws_HttpRequestException() {
		var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("oops") });
		await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).GetVersionAsync());
	}
}
