using System.Net;
using System.Text;
using System.Text.Json;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;
using Aria2NET.Core.Torrents;

namespace Aria2NET.Tests;

public class AddOptionsTests {
	[Fact]
	public void Nothing_filled_in_sends_nothing() {
		Assert.Empty(AddOptions.None.ToOptions());
		Assert.Null(AddOptions.None.Validate());
	}

	[Fact]
	public void Fields_map_to_aria2_option_names() {
		var o = new AddOptions(["Authorization: Bearer abc", "  X-Token: 1  ", ""], "https://example.com/page", "a=b; c=d", "bob", "p@ss", "bob", "ftp-pass", "SHA-256=ABCDEF12", 500, 20).ToOptions();
		Assert.Equal("Authorization: Bearer abc\nX-Token: 1\nCookie: a=b; c=d", o["header"]);
		Assert.Equal("https://example.com/page", o["referer"]);
		Assert.Equal("bob", o["http-user"]);
		Assert.Equal("p@ss", o["http-passwd"]);
		Assert.Equal("bob", o["ftp-user"]);
		Assert.Equal("ftp-pass", o["ftp-passwd"]);
		Assert.Equal("SHA-256=ABCDEF12", o["checksum"]);
		Assert.Equal("500K", o["max-download-limit"]);
		Assert.Equal("20K", o["max-upload-limit"]);
	}

	[Fact]
	public void A_password_without_a_user_is_not_sent() {
		var o = new AddOptions(HttpPassword: "secret").ToOptions();
		Assert.DoesNotContain("http-passwd", o.Keys);
	}

	[Theory]
	[InlineData("no colon here")]
	[InlineData(": value without a name")]
	[InlineData("Name:")]
	[InlineData("Bad Name: value")]
	public void Malformed_headers_are_rejected(string header) => Assert.NotNull(new AddOptions([header]).Validate());

	[Fact]
	public void Control_characters_cannot_smuggle_in_another_header() {
		Assert.NotNull(new AddOptions(["X-A: 1\r\nX-B: 2"]).Validate());
		Assert.NotNull(new AddOptions(Cookie: "a=b\nX-B: 2").Validate());
		Assert.NotNull(new AddOptions(HttpUser: "bob\n", HttpPassword: "x").Validate());
		Assert.NotNull(new AddOptions(Referer: "https://example.com/\nX: y").Validate());
	}

	[Theory]
	[InlineData("sha-256=abcdef12", true)]
	[InlineData("MD5=00ff", true)]
	[InlineData("sha-256=xyz", false)]
	[InlineData("sha-256=abc", false)] // odd number of hex digits
	[InlineData("crc32=00", false)]
	[InlineData("abcdef", false)]
	public void Checksums_need_an_algorithm_and_whole_bytes(string checksum, bool ok) => Assert.Equal(ok, new AddOptions(Checksum: checksum).Validate() is null);

	[Fact]
	public void Referer_must_be_a_web_address_or_star() {
		Assert.Null(new AddOptions(Referer: "*").Validate());
		Assert.Null(new AddOptions(Referer: "http://a.example/x").Validate());
		Assert.NotNull(new AddOptions(Referer: "javascript:alert(1)").Validate());
		Assert.NotNull(new AddOptions(Referer: "not a url").Validate());
	}

	[Fact]
	public void The_record_never_prints_its_secrets() {
		var text = new AddOptions(["Authorization: topsecret"], Cookie: "session=topsecret", HttpUser: "u", HttpPassword: "topsecret").ToString();
		Assert.DoesNotContain("topsecret", text);
	}

	[Fact]
	public async Task The_rpc_client_sends_headers_as_a_json_array() {
		var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) {
			Content = new StringContent("""{"jsonrpc":"2.0","id":"1","result":"0000000000000001"}""", Encoding.UTF8, "application/json"),
		});
		var client = new Aria2RpcClient(new HttpClient(handler), new Uri("http://127.0.0.1:1/jsonrpc"), "s");
		await client.AddUriAsync(["http://x/y"], new Dictionary<string, string> { ["dir"] = "C:\\dl", ["header"] = "A: 1\nB: 2" });

		using var doc = JsonDocument.Parse(handler.Bodies[0]);
		var options = doc.RootElement.GetProperty("params")[2];
		Assert.Equal("C:\\dl", options.GetProperty("dir").GetString());
		Assert.Equal(["A: 1", "B: 2"], options.GetProperty("header").EnumerateArray().Select(e => e.GetString()!).ToArray());
	}

	[Fact]
	public void Torrent_requests_carry_the_extra_options_and_seeding_wins_over_them() {
		var request = new TorrentAddRequest(@"C:\dl", Seeding: new SeedingOverride(SeedMode.Never), Extra: new AddOptions(MaxDownloadKiBps: 100));
		var options = request.ToOptions();
		Assert.Equal("100K", options["max-download-limit"]);
		Assert.Equal("0", options["seed-time"]);
	}
}

public class HeaderPresetsTests {
	private static (HeaderPresets Presets, string Path) Create(TempDir temp) {
		var path = Path.Combine(temp.Path, "presets.json");
		return (new HeaderPresets(new SettingsStore<HeaderPresetFile>(path, HeaderPresetJsonContext.Default.HeaderPresetFile)), path);
	}

	[Fact]
	public void Saves_lists_replaces_and_deletes_presets() {
		using var temp = new TempDir();
		var (presets, _) = Create(temp);

		Assert.Null(presets.Save("Site B", ["X-A: 1"], "https://b.example/", null));
		Assert.Null(presets.Save("site a", ["X-A: 2"], null, "k=v"));
		Assert.Equal(["site a", "Site B"], presets.List().Select(p => p.Name).ToArray());

		Assert.Null(presets.Save("SITE B", ["X-A: 3"], null, null)); // same name, other case: replaced
		Assert.Equal(2, presets.List().Count);
		Assert.Equal(["X-A: 3"], presets.List().First(p => p.Name == "SITE B").Headers);

		presets.Delete("site a");
		Assert.Single(presets.List());
	}

	[Fact]
	public void The_cookie_is_stored_protected_not_as_plain_text() {
		using var temp = new TempDir();
		var (presets, path) = Create(temp);
		Assert.Null(presets.Save("Site", ["X-A: 1"], null, "session=topsecret"));

		Assert.DoesNotContain("topsecret", File.ReadAllText(path));
		Assert.Equal("session=topsecret", HeaderPresets.Cookie(presets.List()[0]));
	}

	[Fact]
	public void Rejects_empty_names_empty_presets_and_bad_headers() {
		using var temp = new TempDir();
		var (presets, _) = Create(temp);
		Assert.NotNull(presets.Save("", ["X-A: 1"], null, null));
		Assert.NotNull(presets.Save("Empty", [], null, null));
		Assert.NotNull(presets.Save("Bad", ["not a header"], null, null));
		Assert.NotNull(presets.Save(new string('x', HeaderPresets.MaxNameLength + 1), ["X-A: 1"], null, null));
		Assert.Empty(presets.List());
	}
}

[Collection(RealAria2Collection.Name)]
public class AddOptionsIntegrationTests {
	[RequiresAria2Fact]
	public async Task Headers_referer_and_login_reach_the_server_and_a_limit_is_applied() {
		var seen = new List<(string? Custom, string? Other, string? Cookie, string? Referer, string? Authorization)>();
		using var server = DownloadIntegrationTests.StartServer(out var prefix, async ctx => {
			lock (seen) {
				seen.Add((ctx.Request.Headers["X-Custom"], ctx.Request.Headers["X-Other"], ctx.Request.Headers["Cookie"], ctx.Request.Headers["Referer"], ctx.Request.Headers["Authorization"]));
			}
			var body = "hello"u8.ToArray();
			ctx.Response.ContentLength64 = body.Length;
			await ctx.Response.OutputStream.WriteAsync(body);
			ctx.Response.Close();
		});
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var outDir = Path.Combine(temp.Path, "out");

		var extra = new AddOptions(["X-Custom: one", "X-Other: two"], "https://ref.example/page", "s=1", HttpUser: "bob", HttpPassword: "pw", MaxDownloadKiBps: 300);
		var options = extra.ToOptions().ToDictionary(p => p.Key, p => p.Value);
		options["dir"] = outDir;
		var gid = await engine.Rpc!.AddUriAsync([prefix + "f.txt"], options);
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(outDir, "f.txt")));
		var request = seen.First();
		Assert.Equal("one", request.Custom);
		Assert.Equal("two", request.Other);
		Assert.Equal("s=1", request.Cookie);
		Assert.Equal("https://ref.example/page", request.Referer);
		var applied = await engine.Rpc.GetOptionAsync(gid);
		Assert.Equal("bob", applied["http-user"]); // aria2 only sends the login after a 401 challenge, which this server never makes
		Assert.Equal(300 * 1024, long.Parse(applied["max-download-limit"]));
	}
}
