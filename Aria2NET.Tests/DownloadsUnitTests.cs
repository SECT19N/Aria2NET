using System.Net;
using System.Text;
using System.Text.Json;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;

namespace Aria2NET.Tests;

public class DownloadsUnitTests {
	private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

	[Fact]
	public void UrlParser_splits_trims_dedupes_and_sorts() {
		var r = UrlParser.Parse("  https://a.com/x.zip \r\nhttps://a.com/x.zip\nnot a url\nftp://h/f\nmagnet:?xt=urn:btih:abc\nfile:///c:/x\n\n");
		Assert.Equal(["https://a.com/x.zip", "ftp://h/f", "magnet:?xt=urn:btih:abc"], r.Valid);
		Assert.Equal(["not", "a", "url", "file:///c:/x"], r.Invalid);
	}

	[Theory]
	[InlineData("http://", false)]
	[InlineData("magnet:?dn=x", false)]
	[InlineData("HTTPS://Example.com", true)]
	[InlineData("sftp://host/file", true)]
	public void UrlParser_IsSupported(string token, bool expected) => Assert.Equal(expected, UrlParser.IsSupported(token));

	[Theory]
	[InlineData(0, "0 B")]
	[InlineData(1023, "1023 B")]
	[InlineData(1536, "1.50 KiB")]
	[InlineData(10L * 1024 * 1024, "10.0 MiB")]
	[InlineData(5L * 1024 * 1024 * 1024, "5.00 GiB")]
	[InlineData(-1, "—")]
	public void ByteFormat_Size(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Size(bytes));

	[Fact]
	public void ByteFormat_Speed_has_suffix() => Assert.Equal("2.00 MiB/s", ByteFormat.Speed(2 * 1024 * 1024));

	[Theory]
	[InlineData(null, "--")]
	[InlineData(5.0, "5s")]
	[InlineData(192.0, "3m 12s")]
	[InlineData(3900.0, "1h 05m")]
	[InlineData(183600.0, "2d 3h")]
	public void TimeFormat_Eta(double? seconds, string expected) =>
		Assert.Equal(expected, TimeFormat.Eta(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value)));

	[Fact]
	public void DownloadItem_parses_active_download() {
		var item = DownloadItem.FromJson(Json("""
			{"gid":"2089b05ecca3d829","status":"active","totalLength":"1000","completedLength":"250","downloadSpeed":"50","dir":"C:/dl",
			 "files":[{"path":"C:/dl/file.zip","uris":[{"uri":"https://x/y/file.zip","status":"used"}]}]}
			"""));
		Assert.Equal("file.zip", item.Name);
		Assert.Equal(DownloadStatus.Active, item.Status);
		Assert.Equal(25.0, item.Progress);
		Assert.Equal(TimeSpan.FromSeconds(15), item.Eta);
		Assert.Equal("https://x/y/file.zip", item.FirstUri);
	}

	[Fact]
	public void DownloadItem_name_falls_back_to_decoded_uri_then_gid() {
		var fromUri = DownloadItem.FromJson(Json("""{"gid":"g1","status":"waiting","files":[{"path":"","uris":[{"uri":"https://x/a%20b.txt"}]}]}"""));
		Assert.Equal("a b.txt", fromUri.Name);
		Assert.Null(fromUri.Eta);

		var noFiles = DownloadItem.FromJson(Json("""{"gid":"g2","status":"paused"}"""));
		Assert.Equal("g2", noFiles.Name);
		Assert.Equal(DownloadStatus.Paused, noFiles.Status);
		Assert.Equal(0, noFiles.Progress);
	}

	[Fact]
	public void DownloadItem_parses_error_and_magnet_names() {
		var err = DownloadItem.FromJson(Json("""{"gid":"g","status":"error","errorCode":"3","errorMessage":"Resource not found","files":[]}"""));
		Assert.Equal(DownloadStatus.Error, err.Status);
		Assert.Equal("Resource not found", err.ErrorMessage);

		var magnet = DownloadItem.FromJson(Json("""{"gid":"m","status":"waiting","files":[{"path":"","uris":[{"uri":"magnet:?xt=urn:btih:abc&dn=My+Linux+ISO"}]}]}"""));
		Assert.Equal("My Linux ISO", magnet.Name);
	}

	private static Aria2RpcClient Client(FakeHandler handler) =>
		new(new HttpClient(handler), new Uri("http://127.0.0.1:1/jsonrpc"), "sekret");

	private static HttpResponseMessage Ok(string body, HttpStatusCode code = HttpStatusCode.OK) =>
		new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Fact]
	public async Task AddUri_sends_token_uris_and_options_and_returns_gid() {
		var handler = new FakeHandler((_, _) => Ok("""{"jsonrpc":"2.0","id":"1","result":"abc123"}"""));
		var gid = await Client(handler).AddUriAsync(["https://x/f"], new Dictionary<string, string> { ["dir"] = "C:/dl", ["out"] = "f.bin" });

		Assert.Equal("abc123", gid);
		using var req = JsonDocument.Parse(handler.Bodies.Single());
		Assert.Equal("aria2.addUri", req.RootElement.GetProperty("method").GetString());
		var p = req.RootElement.GetProperty("params");
		Assert.Equal("token:sekret", p[0].GetString());
		Assert.Equal("https://x/f", p[1][0].GetString());
		Assert.Equal("f.bin", p[2].GetProperty("out").GetString());
	}

	[Fact]
	public async Task Poll_sends_one_multicall_with_token_inside_each_call_only() {
		var handler = new FakeHandler((_, _) => Ok("""
			{"jsonrpc":"2.0","id":"1","result":[
			  [[{"gid":"a","status":"active","totalLength":"10","completedLength":"5","downloadSpeed":"1"}]],
			  [[{"gid":"b","status":"waiting"}]],
			  [[{"gid":"c","status":"complete","totalLength":"3","completedLength":"3"}]],
			  [{"downloadSpeed":"1","numActive":"1","numWaiting":"1","numStopped":"1"}]]}
			"""));
		var snapshot = await Client(handler).PollAsync();

		Assert.Equal(["a", "b", "c"], snapshot.Items.Select(i => i.Gid));
		Assert.Equal(new GlobalStats(1, 1, 1, 1), snapshot.Stats);

		using var req = JsonDocument.Parse(handler.Bodies.Single());
		Assert.Equal("system.multicall", req.RootElement.GetProperty("method").GetString());
		var outer = req.RootElement.GetProperty("params");
		Assert.Equal(1, outer.GetArrayLength());
		var calls = outer[0];
		Assert.Equal(4, calls.GetArrayLength());
		Assert.All(calls.EnumerateArray(), c => Assert.Equal("token:sekret", c.GetProperty("params")[0].GetString()));
		Assert.Equal("aria2.tellStopped", calls[2].GetProperty("methodName").GetString());
	}

	[Fact]
	public async Task Poll_maps_a_failed_multicall_entry_to_an_exception() {
		var handler = new FakeHandler((_, _) => Ok("""{"jsonrpc":"2.0","id":"1","result":[{"code":1,"message":"boom"},[[]],[[]],[{}]]}"""));
		var ex = await Assert.ThrowsAsync<Aria2RpcException>(() => Client(handler).PollAsync());
		Assert.Equal("boom", ex.Message);
	}

	[Fact]
	public async Task Rpc_error_body_on_http_400_becomes_exception() {
		var handler = new FakeHandler((_, _) => Ok("""{"jsonrpc":"2.0","id":"1","error":{"code":1,"message":"Unauthorized"}}""", HttpStatusCode.BadRequest));
		var ex = await Assert.ThrowsAsync<Aria2RpcException>(() => Client(handler).GetGlobalStatAsync());
		Assert.Equal("Unauthorized", ex.Message);
	}
}
