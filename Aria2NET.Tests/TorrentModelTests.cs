using System.Net;
using System.Text;
using System.Text.Json;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Torrents;

namespace Aria2NET.Tests;

public class TorrentModelTests {
	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

	private static DownloadItem Parse(string text) => DownloadItem.FromJson(Json(text));

	private const string SeedingStatus = """
		{"gid":"s1","status":"active","seeder":"true","totalLength":"1000","completedLength":"1000","downloadSpeed":"0",
		 "uploadSpeed":"2048","uploadLength":"3000","numSeeders":"0","connections":"4","infoHash":"2B23B147743538B694A5049FC9F9BA676000E7D4",
		 "dir":"C:\\dl","bittorrent":{"mode":"multi","info":{"name":"My Album"}},
		 "files":[{"index":"1","path":"C:/dl/My Album/01.mp3","length":"500","selected":"true","uris":[]},{"index":"2","path":"C:/dl/My Album/02.mp3","length":"500","selected":"true","uris":[]}]}
		""";

	[Fact]
	public void Active_with_seeder_true_is_Seeding_and_carries_torrent_fields() {
		var item = Parse(SeedingStatus);

		Assert.Equal(DownloadStatus.Seeding, item.Status);
		Assert.True(item.IsTorrent);
		Assert.Equal("2b23b147743538b694a5049fc9f9ba676000e7d4", item.InfoHash);
		Assert.Equal(2048, item.UploadSpeedBytesPerSec);
		Assert.Equal(3000, item.UploadedBytes);
		Assert.Equal(4, item.Connections);
		Assert.Equal(3.0, item.ShareRatio);
		Assert.Null(item.Eta);
	}

	[Fact]
	public void Torrent_is_named_after_the_torrent_not_its_first_file() {
		Assert.Equal("My Album", Parse(SeedingStatus).Name);
	}

	[Fact]
	public void Download_without_the_seeder_flag_stays_Active() {
		var item = Parse("""{"gid":"d","status":"active","seeder":"false","infoHash":"abc","bittorrent":{"info":{"name":"x"}},"files":[]}""");

		Assert.Equal(DownloadStatus.Active, item.Status);
		Assert.True(item.IsTorrent);
	}

	[Fact]
	public void Plain_http_download_is_not_a_torrent() {
		var item = Parse("""{"gid":"h","status":"active","files":[{"path":"C:/dl/a.zip","uris":[{"uri":"https://x/a.zip"}]}]}""");

		Assert.False(item.IsTorrent);
		Assert.Null(item.InfoHash);
		Assert.Equal("a.zip", item.Name);
	}

	[Fact]
	public void Magnet_placeholder_is_recognised_as_fetching_metadata_and_has_no_openable_path() {
		var item = Parse("""
			{"gid":"m","status":"active","infoHash":"4CDD3A2EECBFD4A4C3CF40E93963A2F6957E1B7D","bittorrent":{"announceList":[["http://t/announce"]]},
			 "totalLength":"0","completedLength":"0","files":[{"index":"1","path":"[METADATA]lonely","length":"0","uris":[]}],"followedBy":["f1"]}
			""");

		Assert.True(item.IsFetchingMetadata);
		Assert.Equal("lonely", item.Name);
		Assert.Null(item.Path);
		Assert.True(item.IsTorrent);
		Assert.Equal("f1", item.FollowedBy);
	}

	[Fact]
	public void Finished_metadata_placeholder_is_no_longer_fetching() {
		var item = Parse("""{"gid":"m","status":"complete","infoHash":"abc","bittorrent":{},"files":[{"path":"[METADATA]x"}]}""");

		Assert.False(item.IsFetchingMetadata);
		Assert.True(item.IsMetadata);
	}

	[Fact]
	public void Metadata_placeholder_without_a_name_falls_back_to_the_info_hash() {
		var item = Parse("""{"gid":"m","status":"active","infoHash":"abcdef","bittorrent":{},"files":[{"path":"[METADATA]"}]}""");

		Assert.Equal("abcdef", item.Name);
	}

	[Fact]
	public void Global_stats_read_the_upload_speed() {
		var stats = GlobalStats.FromJson(Json("""{"downloadSpeed":"10","uploadSpeed":"20","numActive":"1","numWaiting":"2","numStopped":"3"}"""));

		Assert.Equal(20, stats.UploadSpeed);
		Assert.Equal(10, stats.DownloadSpeed);
	}

	[Fact]
	public void Seeding_counts_as_live_and_can_be_paused_and_limited() {
		var seeding = Parse(SeedingStatus);

		Assert.True(DownloadCapabilities.IsLive(seeding));
		Assert.True(DownloadCapabilities.CanPause(seeding));
		Assert.True(DownloadCapabilities.CanSetSpeedLimit(seeding));
		Assert.False(DownloadCapabilities.CanResume(seeding));
		Assert.False(DownloadCapabilities.CanRetry(seeding));
	}

	[Fact]
	public void Filters_put_seeding_torrents_under_Seeding_and_Torrents_but_not_Downloading() {
		var seeding = Parse(SeedingStatus);
		var http = Parse("""{"gid":"h","status":"active","files":[{"path":"C:/dl/a.zip"}]}""");

		Assert.True(DownloadView.Matches(StatusFilter.Seeding, seeding));
		Assert.True(DownloadView.Matches(StatusFilter.Torrents, seeding));
		Assert.False(DownloadView.Matches(StatusFilter.Downloading, seeding));
		Assert.True(DownloadView.Matches(StatusFilter.All, seeding));
		Assert.False(DownloadView.Matches(StatusFilter.Torrents, http));
		Assert.False(DownloadView.Matches(StatusFilter.Seeding, http));
	}

	[Fact]
	public void Recheck_and_seeding_rules_apply_to_live_torrents_only() {
		var seeding = Parse(SeedingStatus);
		var http = Parse("""{"gid":"h","status":"active","files":[{"path":"C:/dl/a.zip"}]}""");
		var placeholder = Parse("""{"gid":"m","status":"active","infoHash":"abc","bittorrent":{},"files":[{"path":"[METADATA]x"}]}""");
		var finished = Parse("""{"gid":"c","status":"complete","infoHash":"abc","bittorrent":{"info":{"name":"x"}},"files":[{"path":"C:/dl/x"}]}""");

		Assert.True(DownloadCapabilities.CanRecheck(seeding));
		Assert.True(DownloadCapabilities.CanChangeSeeding(seeding));
		Assert.False(DownloadCapabilities.CanRecheck(http));
		Assert.False(DownloadCapabilities.CanRecheck(placeholder));
		Assert.False(DownloadCapabilities.CanRecheck(finished));
	}

	[Fact]
	public void A_multi_file_torrent_has_no_single_file_to_open_but_a_single_file_one_does() {
		using var temp = new TempDir();
		var file = Path.Combine(temp.Path, "one.iso").Replace('\\', '/');
		File.WriteAllText(file, "x");
		var multi = Parse(SeedingStatus.Replace("C:/dl/My Album/01.mp3", file));
		var single = Parse("{\"gid\":\"s\",\"status\":\"complete\",\"infoHash\":\"abc\",\"bittorrent\":{\"info\":{\"name\":\"one.iso\"}},\"files\":[{\"path\":\"" + file + "\"}]}");

		Assert.Equal(2, multi.FileCount);
		Assert.False(DownloadCapabilities.CanOpenFile(multi));
		Assert.True(DownloadCapabilities.CanOpenFile(single));
	}
}

public class TorrentRpcTests {
	private static Aria2RpcClient Client(FakeHandler handler) =>
		new(new HttpClient(handler), new Uri("http://127.0.0.1:1/jsonrpc"), "s3cret");

	private static HttpResponseMessage Json(string result) =>
		new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"1\",\"jsonrpc\":\"2.0\",\"result\":" + result + "}", Encoding.UTF8, "application/json") };

	private static JsonElement Sent(FakeHandler handler) => JsonDocument.Parse(handler.Bodies.Single()).RootElement.Clone();

	[Fact]
	public async Task AddTorrent_sends_base64_webseeds_and_options_and_returns_the_gid() {
		var handler = new FakeHandler((_, _) => Json("\"gid1\""));
		byte[] torrent = [1, 2, 3, 250];

		var gid = await Client(handler).AddTorrentAsync(torrent, new Dictionary<string, string> { ["pause"] = "true" }, ["http://seed/x"]);

		Assert.Equal("gid1", gid);
		var sent = Sent(handler);
		var p = sent.GetProperty("params");
		Assert.Equal("aria2.addTorrent", sent.GetProperty("method").GetString());
		Assert.Equal("token:s3cret", p[0].GetString());
		Assert.Equal(Convert.ToBase64String(torrent), p[1].GetString());
		Assert.Equal("http://seed/x", p[2][0].GetString());
		Assert.Equal("true", p[3].GetProperty("pause").GetString());
	}

	[Fact]
	public async Task AddTorrent_without_options_still_sends_an_empty_uri_list_and_options_object() {
		var handler = new FakeHandler((_, _) => Json("\"g\""));

		await Client(handler).AddTorrentAsync([1]);

		var p = Sent(handler).GetProperty("params");
		Assert.Equal(JsonValueKind.Array, p[2].ValueKind);
		Assert.Equal(0, p[2].GetArrayLength());
		Assert.Equal(JsonValueKind.Object, p[3].ValueKind);
	}

	[Fact]
	public async Task AddMetalink_returns_every_gid() {
		var handler = new FakeHandler((_, _) => Json("[\"a\",\"b\"]"));

		var gids = await Client(handler).AddMetalinkAsync([9, 9], new Dictionary<string, string> { ["dir"] = "C:\\dl" });

		Assert.Equal(["a", "b"], gids);
		var sent = Sent(handler);
		var p = sent.GetProperty("params");
		Assert.Equal("aria2.addMetalink", sent.GetProperty("method").GetString());
		Assert.Equal(Convert.ToBase64String([9, 9]), p[1].GetString());
		Assert.Equal("C:\\dl", p[2].GetProperty("dir").GetString());
	}

	[Fact]
	public async Task GetFiles_parses_index_selection_and_progress() {
		var handler = new FakeHandler((_, _) => Json("""[{"index":"1","path":"a/b.bin","length":"100","completedLength":"40","selected":"true"},{"index":"2","path":"c.bin","length":"5","completedLength":"0","selected":"false"}]"""));

		var files = await Client(handler).GetFilesAsync("g");

		Assert.Equal(new DownloadFile(1, "a/b.bin", 100, 40, true), files[0]);
		Assert.False(files[1].Selected);
		Assert.Equal("g", Sent(handler).GetProperty("params")[1].GetString());
	}

	[Fact]
	public async Task GetPeers_parses_and_decodes_the_peer_id() {
		var handler = new FakeHandler((_, _) => Json("""[{"peerId":"-qB4650-%AB%CD","ip":"10.0.0.2","port":"6881","bitfield":"ff","amChoking":"false","peerChoking":"true","downloadSpeed":"7","uploadSpeed":"9","seeder":"true"}]"""));

		var peer = Assert.Single(await Client(handler).GetPeersAsync("g"));

		Assert.StartsWith("-qB4650-", peer.PeerId);
		Assert.Equal("10.0.0.2", peer.Ip);
		Assert.Equal(6881, peer.Port);
		Assert.True(peer.PeerChoking);
		Assert.False(peer.AmChoking);
		Assert.True(peer.Seeder);
		Assert.Equal(7, peer.DownloadSpeed);
		Assert.Equal(9, peer.UploadSpeed);
	}

	[Fact]
	public async Task Poll_asks_for_the_torrent_status_keys() {
		var handler = new FakeHandler((_, _) => Json("""[[[]],[[]],[[]],[{"downloadSpeed":"0","uploadSpeed":"5"}]]"""));

		var snapshot = await Client(handler).PollAsync();

		Assert.Equal(5, snapshot.Stats.UploadSpeed);
		var body = handler.Bodies.Single();
		foreach (var key in new[] { "infoHash", "seeder", "uploadSpeed", "uploadLength", "numSeeders", "connections", "followedBy", "belongsTo" }) {
			Assert.Contains("\"" + key + "\"", body);
		}
	}
}

public class TorrentSummaryTests {
	private static DownloadItem T(DownloadStatus status, int connections, bool metadata = false) =>
		new("g", "n", status, 1, 1, 0, null, null, null, IsTorrent: true, Connections: connections, IsMetadata: metadata);

	[Fact]
	public void Says_nothing_without_running_torrents() {
		Assert.Equal("", TorrentSummary.Describe(new PollSnapshot([T(DownloadStatus.Paused, 0), T(DownloadStatus.Complete, 0)], GlobalStats.Empty)));
		Assert.Equal("", TorrentSummary.Describe(new PollSnapshot([], GlobalStats.Empty)));
	}

	[Fact]
	public void Summarises_seeding_upload_speed_and_peers() {
		var snapshot = new PollSnapshot([T(DownloadStatus.Seeding, 4), T(DownloadStatus.Active, 7), T(DownloadStatus.Seeding, 1, metadata: true)], new GlobalStats(0, 2, 0, 0, 2048));

		Assert.Equal("1 seeding · ↑ 2.00 KiB/s · 11 peers", TorrentSummary.Describe(snapshot));
	}

	[Fact]
	public void Leaves_out_the_seeding_part_when_nothing_seeds() {
		var text = TorrentSummary.Describe(new PollSnapshot([T(DownloadStatus.Active, 3)], GlobalStats.Empty));

		Assert.StartsWith("↑ ", text);
		Assert.EndsWith(" · 3 peers", text);
		Assert.DoesNotContain("seeding", text);
	}
}
