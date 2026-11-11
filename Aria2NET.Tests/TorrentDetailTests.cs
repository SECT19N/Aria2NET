using System.Text.Json;
using Aria2NET.Core.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class PeerIdClientTests {
	[Theory]
	[InlineData("-qB4650-abcdefghijkl", "qBittorrent 4.6.5")]
	[InlineData("-TR3000-abcdefghijkl", "Transmission 3")]
	[InlineData("-UT355S-abcdefghijkl", "µTorrent 3.5.5.S")]
	[InlineData("-DE2110-abcdefghijkl", "Deluge 2.1.1")]
	[InlineData("-ZZ9999-abcdefghijkl", "Unknown")]
	[InlineData("aria2/1.37.0-xyz", "aria2")]
	[InlineData("", "Unknown")]
	[InlineData(null, "Unknown")]
	[InlineData("short", "Unknown")]
	public void Names_the_client_from_the_peer_id(string? peerId, string expected) {
		Assert.Equal(expected, PeerIdClient.Name(peerId));
	}
}

public class BitfieldTests {
	[Fact]
	public void Decodes_hex_with_the_highest_bit_first() {
		Assert.Equal([true, false, true, false, true, true, false, false], Bitfield.Decode("ac", 8));
	}

	[Fact]
	public void Pads_and_trims_to_the_piece_count() {
		Assert.Equal([true, true, true, true, false, false], Bitfield.Decode("f", 6));
		Assert.Equal([true, true], Bitfield.Decode("ffff", 2));
		Assert.Equal(new bool[3], Bitfield.Decode(null, 3));
		Assert.Empty(Bitfield.Decode("ff", 0));
	}

	[Fact]
	public void Counts_the_pieces_a_peer_has() {
		Assert.Equal(5, Bitfield.Count("f8", 8));
		Assert.Equal(4, Bitfield.Count("ff", 4)); // bits past the last piece do not count
	}

	[Fact]
	public void Downsample_averages_pieces_into_buckets() {
		Assert.Equal([1.0, 0.0], Bitfield.Downsample([1, 1, 0, 0], 2));
		Assert.Equal([0.5, 0.5], Bitfield.Downsample([1, 0, 1, 0], 2));
	}

	[Fact]
	public void Downsample_can_stretch_a_few_pieces_over_many_buckets() {
		var bar = Bitfield.Downsample([1, 0], 4);

		Assert.Equal([1.0, 1.0, 0.0, 0.0], bar);
		Assert.Empty(Bitfield.Downsample([1], 0));
		Assert.All(Bitfield.Downsample([], 3), v => Assert.Equal(0, v));
	}
}

public class GeoIpResolverTests {
	private const string Csv = """
		"1.0.0.0","1.0.0.255","AU"
		"8.8.8.0","8.8.8.255","US"
		"81.2.69.0","81.2.69.255","GB"
		"2001:db8::","2001:db8:ffff:ffff:ffff:ffff:ffff:ffff","DE"
		garbage line
		"9.9.9.0","9.9.9.255","TOOLONG"
		""";

	[Fact]
	public void Finds_the_country_of_an_address() {
		var geo = GeoIpResolver.FromCsv(new StringReader(Csv));

		Assert.Equal("US", geo.Lookup("8.8.8.8"));
		Assert.Equal("AU", geo.Lookup("1.0.0.0"));
		Assert.Equal("AU", geo.Lookup("1.0.0.255"));
		Assert.Equal("GB", geo.Lookup("81.2.69.142"));
		Assert.Equal("DE", geo.Lookup("2001:db8:1::1"));
		Assert.Equal(4, geo.RangeCount);
	}

	[Theory]
	[InlineData("1.0.1.0")]
	[InlineData("0.0.0.1")]
	[InlineData("255.255.255.255")]
	[InlineData("9.9.9.9")]
	[InlineData("2001:db9::1")]
	[InlineData("not an ip")]
	[InlineData("")]
	[InlineData(null)]
	public void Gives_null_outside_every_range_or_for_nonsense(string? ip) {
		Assert.Null(GeoIpResolver.FromCsv(new StringReader(Csv)).Lookup(ip));
	}

	[Fact]
	public void An_ipv4_address_in_ipv6_form_is_looked_up_as_ipv4() {
		Assert.Equal("US", GeoIpResolver.FromCsv(new StringReader(Csv)).Lookup("::ffff:8.8.8.8"));
	}

	[Fact]
	public void Reads_gzip_and_survives_a_missing_or_broken_file() {
		using var temp = new TempDir();
		var gz = Path.Combine(temp.Path, "db.csv.gz");
		using (var file = File.Create(gz))
		using (var zip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress))
		using (var writer = new StreamWriter(zip)) {
			writer.Write(Csv);
		}
		var broken = Path.Combine(temp.Path, "broken.csv.gz");
		File.WriteAllText(broken, "this is not gzip");

		Assert.Equal("US", GeoIpResolver.Load(gz).Lookup("8.8.8.8"));
		Assert.Null(GeoIpResolver.Load(Path.Combine(temp.Path, "nothing.csv.gz")).Lookup("8.8.8.8"));
		Assert.Null(GeoIpResolver.Load(broken).Lookup("8.8.8.8"));
	}

	[Fact]
	public void Country_names_fall_back_to_the_code() {
		Assert.Equal("Germany", GeoIpResolver.CountryName("DE"));
		Assert.Equal("", GeoIpResolver.CountryName(null));
	}
}

public class TorrentDetailBuildTests {
	[Fact]
	public void Builds_overview_trackers_peers_and_bars_from_aria2_output() {
		using var doc = JsonDocument.Parse("""
			{"gid":"g","status":"active","seeder":"false","infoHash":"ABCDEF","dir":"C:\\dl","totalLength":"1000","completedLength":"500",
			 "uploadLength":"250","downloadSpeed":"100","uploadSpeed":"50","numSeeders":"3","connections":"2","numPieces":"8","pieceLength":"125",
			 "bitfield":"f0",
			 "bittorrent":{"comment":"hello","creationDate":"1700000000","announceList":[["http://a/announce","udp://b:1/x"],["http://c/z"]],"info":{"name":"Album"}}}
			""");
		var peers = new List<PeerInfo> {
			new("-qB4650-xxxxxxxxxxxx", "8.8.8.8", 6881, "ff", false, false, 10, 20, true),
			new("-TR3000-xxxxxxxxxxxx", "10.0.0.2", 51413, "0f", false, true, 0, 0, false),
		};
		var geo = GeoIpResolver.FromCsv(new StringReader("\"8.8.8.0\",\"8.8.8.255\",\"US\""));

		var snapshot = TorrentDetailLoader.Build(doc.RootElement, [new DownloadFile(1, "a", 1000, 500, true)], peers, geo);

		Assert.Equal("Album", snapshot.Name);
		Assert.Equal("abcdef", snapshot.InfoHash);
		Assert.Equal("hello", snapshot.Comment);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), snapshot.CreatedAt);
		Assert.Equal(0.5, snapshot.Ratio);
		Assert.Equal(50, snapshot.Progress);
		Assert.Equal(8, snapshot.PieceCount);
		Assert.Equal([new TorrentTracker("http://a/announce", 1), new TorrentTracker("udp://b:1/x", 1), new TorrentTracker("http://c/z", 2)], snapshot.Trackers);

		Assert.Equal(2, snapshot.Peers.Count);
		Assert.Equal(new TorrentPeer("8.8.8.8:6881", "qBittorrent 4.6.5", "US", 10, 20, 100, true, false), snapshot.Peers[0]);
		Assert.Equal(50, snapshot.Peers[1].Progress);
		Assert.Null(snapshot.Peers[1].Country);
		Assert.True(snapshot.Peers[1].ChokesUs);

		// 8 pieces over 240 buckets: the first four pieces are ours.
		Assert.Equal(TorrentDetailLoader.BarBuckets, snapshot.HaveBar.Length);
		Assert.Equal(1.0, snapshot.HaveBar[0]);
		Assert.Equal(0.0, snapshot.HaveBar[^1]);
		// Pieces 0-3 are held by one peer, 4-7 by both... the first peer has everything, the second only the last four.
		Assert.Equal(0.5, snapshot.AvailabilityBar[0]);
		Assert.Equal(1.0, snapshot.AvailabilityBar[^1]);
	}

	[Fact]
	public void A_torrent_without_peers_or_pieces_yields_empty_bars_not_errors() {
		using var doc = JsonDocument.Parse("""{"gid":"g","status":"waiting"}""");

		var snapshot = TorrentDetailLoader.Build(doc.RootElement, [], [], GeoIpResolver.Empty);

		Assert.Empty(snapshot.Peers);
		Assert.Empty(snapshot.Trackers);
		Assert.All(snapshot.HaveBar, v => Assert.Equal(0, v));
		Assert.Equal("waiting", snapshot.Status);
		Assert.Equal(0, snapshot.Ratio);
	}

	[Fact]
	public void A_seeder_flag_turns_the_status_into_seeding() {
		using var doc = JsonDocument.Parse("""{"gid":"g","status":"active","seeder":"true"}""");

		Assert.Equal("seeding", TorrentDetailLoader.Build(doc.RootElement, [], [], GeoIpResolver.Empty).Status);
	}
}

[Collection(RealAria2Collection.Name)]
public class TorrentDetailIntegrationTests {
	[RequiresAria2Fact]
	public async Task Detail_of_a_downloading_torrent_shows_the_seeder_and_a_partly_filled_piece_bar() {
		// The seeder is throttled so the download is still running while we look at it.
		using var swarm = new Swarm([("big.bin", 800_000)], seederUploadLimit: "40K");
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = new TorrentOperations(engine, NullLogger<TorrentOperations>.Instance);
		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir));

		TorrentDetailSnapshot? snapshot = null;
		var deadline = DateTime.UtcNow.AddSeconds(25);
		while (DateTime.UtcNow < deadline) {
			snapshot = await TorrentDetailLoader.LoadAsync(engine.Rpc!, gid, GeoIpResolver.Empty);
			if (snapshot.Peers.Count > 0 && snapshot.CompletedBytes > 0) {
				break;
			}
			await Task.Delay(300);
		}

		Assert.NotNull(snapshot);
		Assert.Equal(swarm.Torrent.InfoHash, snapshot.InfoHash);
		Assert.Equal("swarm", snapshot.Name);
		Assert.Equal(800_000, snapshot.TotalBytes);
		Assert.NotEmpty(snapshot.Peers);
		Assert.True(snapshot.Peers[0].IsSeeder, "the seeder has every piece");
		Assert.Equal(100, snapshot.Peers[0].Progress);
		Assert.StartsWith("127.0.0.1:", snapshot.Peers[0].Address);
		Assert.Contains(snapshot.Trackers, t => t.Url == swarm.Tracker.AnnounceUrl);
		Assert.Single(snapshot.Files);
		Assert.Equal(1.0, snapshot.AvailabilityBar.Max());
		Assert.True(snapshot.HaveBar.Sum() > 0 && snapshot.HaveBar.Min() < 1, "some of the torrent is here, not all");
		Assert.True(snapshot.PieceCount > 0);
	}

	[RequiresAria2Fact]
	public async Task A_tracker_cannot_be_added_to_a_torrent_that_already_exists() {
		using var swarm = new Swarm([("big.bin", 800_000)], seederUploadLimit: "40K");
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = new TorrentOperations(engine, NullLogger<TorrentOperations>.Instance);
		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir), paused: true);
		await engine.Rpc!.ChangeOptionAsync(gid, new Dictionary<string, string> { ["bt-tracker"] = "udp://extra.example:1/announce" });
		await engine.Rpc.UnpauseAsync([gid]);
		await Task.Delay(1500);

		var snapshot = await TorrentDetailLoader.LoadAsync(engine.Rpc, gid, GeoIpResolver.Empty);

		// aria2 applies bt-tracker when the torrent is added; changing it afterwards does not touch the tracker list (checked against 1.37.0).
		Assert.DoesNotContain(snapshot.Trackers, t => t.Url == "udp://extra.example:1/announce");
		Assert.Contains(snapshot.Trackers, t => t.Url == swarm.Tracker.AnnounceUrl);
	}

	[Fact]
	public void The_real_database_resolves_well_known_addresses_when_it_has_been_fetched() {
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		string? file = null;
		for (; dir is not null && file is null; dir = dir.Parent) {
			var candidate = Path.Combine(dir.FullName, "tools", "geoip", "dbip-country-lite.csv.gz");
			file = File.Exists(candidate) ? candidate : null;
		}
		if (file is null) {
			return; // optional download (tools/get-geoip.ps1); nothing to check
		}

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var geo = GeoIpResolver.Load(file);
		watch.Stop();

		Assert.True(geo.RangeCount > 100_000);
		Assert.Equal("US", geo.Lookup("8.8.8.8"));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"loading took {watch.Elapsed}");
	}
}
