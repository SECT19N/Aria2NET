using System.Text;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class TorrentParsingTests {
	[Fact]
	public void Reads_name_hash_files_and_trackers_of_a_multi_file_torrent() {
		var torrent = TestTorrent.Create("album", "http://tracker.example/announce", ("a.bin", 40_000), ("sub/b.bin", 60_000));

		var meta = TorrentMetadata.Parse(torrent.TorrentFile);

		Assert.Equal("album", meta.Name);
		Assert.Equal(torrent.InfoHash, meta.InfoHash);
		Assert.Equal([new TorrentFileEntry(1, "a.bin", 40_000), new TorrentFileEntry(2, "sub/b.bin", 60_000)], meta.Files);
		Assert.Equal(100_000, meta.TotalBytes);
		Assert.Equal(["http://tracker.example/announce"], meta.Trackers);
	}

	[Fact]
	public void A_single_file_torrent_has_one_file_named_after_the_torrent() {
		var torrent = TestTorrent.Create("one.iso", "http://t/a", ("one.iso", 20_000));

		var meta = TorrentMetadata.Parse(torrent.TorrentFile);

		Assert.Equal([new TorrentFileEntry(1, "one.iso", 20_000)], meta.Files);
	}

	[Fact]
	public void Announce_list_tiers_are_flattened_without_duplicates() {
		var file = Core.Torrents.Bencode.Encode(new Dictionary<string, object> {
			["announce"] = "http://a/x",
			["announce-list"] = new List<object> { new List<object> { "http://a/x", "udp://b:1/y" }, new List<object> { "http://c/z" } },
			["info"] = new Dictionary<string, object> { ["name"] = "n", ["length"] = 5L, ["piece length"] = 16384L, ["pieces"] = new byte[20] },
		});

		Assert.Equal(["http://a/x", "udp://b:1/y", "http://c/z"], TorrentMetadata.Parse(file).Trackers);
	}

	[Fact]
	public void Prefers_the_utf8_name_fields() {
		var file = Core.Torrents.Bencode.Encode(new Dictionary<string, object> {
			["info"] = new Dictionary<string, object> { ["name"] = new byte[] { 0xE9 }, ["name.utf-8"] = "café", ["length"] = 5L },
		});

		Assert.Equal("café", TorrentMetadata.Parse(file).Name);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not a torrent")]
	[InlineData("d4:spam4:eggse")]
	[InlineData("d4:infod4:name1:xee")]
	[InlineData("d4:infod4:name0:6:lengthi5eee")]
	[InlineData("d4:infod4:name1:x5:filesleee")]
	[InlineData("d4:infod4:name1:x5:filesld6:lengthi-1e4:pathl1:aeeeee")]
	[InlineData("d4:infod4:name1:x5:filesld6:lengthi5eeeee")]
	[InlineData("i42e")]
	public void Rejects_files_that_are_not_usable_torrents(string text) {
		var ex = Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Encoding.Latin1.GetBytes(text)));
		Assert.False(string.IsNullOrWhiteSpace(ex.Message));
	}

	[Fact]
	public void Rejects_oversized_input_without_decoding_it() {
		Assert.Throws<FormatException>(() => TorrentMetadata.Parse(new byte[TorrentMetadata.MaxFileBytes + 1]));
	}
}

public class MagnetAndSelectionTests {
	private const string Hex = "2b23b147743538b694a5049fc9f9ba676000e7d4";

	[Theory]
	[InlineData("magnet:?xt=urn:btih:2B23B147743538B694A5049FC9F9BA676000E7D4&dn=x", Hex)]
	[InlineData("magnet:?dn=x&xt=urn:btih:2b23b147743538b694a5049fc9f9ba676000e7d4&tr=udp%3A%2F%2Ft", Hex)]
	[InlineData("MAGNET:?XT=URN:BTIH:2b23b147743538b694a5049fc9f9ba676000e7d4", Hex)]
	public void Reads_a_hex_info_hash(string magnet, string expected) {
		Assert.True(MagnetLink.TryGetInfoHash(magnet, out var hash));
		Assert.Equal(expected, hash);
	}

	[Fact]
	public void Reads_a_base32_info_hash() {
		// 20 bytes 0x00..0x13 in base32.
		var bytes = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
		var base32 = ToBase32(bytes);

		Assert.True(MagnetLink.TryGetInfoHash("magnet:?xt=urn:btih:" + base32, out var hash));
		Assert.Equal(Convert.ToHexString(bytes).ToLowerInvariant(), hash);
	}

	[Theory]
	[InlineData("https://example.com/a")]
	[InlineData("magnet:?dn=only-a-name")]
	[InlineData("magnet:?xt=urn:btih:tooshort")]
	[InlineData("magnet:?xt=urn:sha1:2b23b147743538b694a5049fc9f9ba676000e7d4")]
	[InlineData("magnet:?xt=urn:btih:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
	public void Gives_no_hash_for_anything_else(string text) {
		Assert.False(MagnetLink.TryGetInfoHash(text, out _));
	}

	[Fact]
	public void Reads_the_display_name() {
		Assert.Equal("My Linux ISO", MagnetLink.DisplayName("magnet:?xt=urn:btih:abc&dn=My+Linux%20ISO"));
		Assert.Null(MagnetLink.DisplayName("magnet:?xt=urn:btih:abc"));
	}

	[Theory]
	[InlineData("1,2,3", 3, null)]
	[InlineData("2", 3, "2")]
	[InlineData("1,3", 5, "1,3")]
	[InlineData("1,2,3,5", 6, "1-3,5")]
	[InlineData("5,3,4,1", 6, "1,3-5")]
	[InlineData("2,2,3", 4, "2-3")]
	[InlineData("1,99,2", 4, "1-2")]
	public void Builds_the_select_file_value(string chosen, int total, string? expected) {
		var indexes = chosen.Split(',').Select(int.Parse);

		Assert.Equal(expected, SelectFileSpec.Build(indexes, total));
	}

	[Fact]
	public void Choosing_nothing_is_an_error() {
		Assert.Throws<ArgumentException>(() => SelectFileSpec.Build([], 3));
		Assert.Throws<ArgumentException>(() => SelectFileSpec.Build([9], 3));
	}

	private static string ToBase32(byte[] bytes) {
		const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		var sb = new StringBuilder();
		int buffer = 0, bits = 0;
		foreach (var b in bytes) {
			buffer = (buffer << 8) | b;
			bits += 8;
			while (bits >= 5) {
				bits -= 5;
				sb.Append(Alphabet[(buffer >> bits) & 31]);
			}
		}
		return sb.ToString();
	}
}

public class LinkKindTests {
	[Theory]
	[InlineData("https://example.com/a.zip", LinkKind.Uri)]
	[InlineData("sftp://host/file", LinkKind.Uri)]
	[InlineData("https://example.com/a.torrent", LinkKind.Uri)]
	[InlineData("magnet:?xt=urn:btih:abc", LinkKind.Magnet)]
	[InlineData("magnet:?dn=x", LinkKind.Unsupported)]
	[InlineData(@"C:\Users\me\Downloads\a.torrent", LinkKind.TorrentFile)]
	[InlineData(@"D:\x\B.TORRENT", LinkKind.TorrentFile)]
	[InlineData(@"C:\x\list.metalink", LinkKind.MetalinkFile)]
	[InlineData(@"C:\x\list.meta4", LinkKind.MetalinkFile)]
	[InlineData(@"C:\x\notes.txt", LinkKind.Unsupported)]
	[InlineData(@"relative\a.torrent", LinkKind.Unsupported)]
	[InlineData("file:///C:/a.torrent", LinkKind.Unsupported)]
	[InlineData("hello", LinkKind.Unsupported)]
	public void Classifies_a_token(string token, LinkKind expected) {
		Assert.Equal(expected, UrlParser.Classify(token));
	}

	[Fact]
	public void Parse_accepts_local_torrent_paths_also_when_quoted() {
		var result = UrlParser.Parse("\"C:\\My Files\\a.torrent\"\r\nhttps://a.com/x\r\nC:\\plain\\b.torrent\r\nC:\\nope\\c.txt");

		Assert.Equal([@"C:\My Files\a.torrent", "https://a.com/x", @"C:\plain\b.torrent"], result.Valid);
		Assert.Equal([@"C:\nope\c.txt"], result.Invalid);
	}
}

public class TorrentRequestTests {
	[Fact]
	public void Seeding_choices_become_aria2_options() {
		Assert.Empty(SeedingOverride.Default.ToOptions());
		Assert.Equal("0.0", new SeedingOverride(SeedMode.Forever).ToOptions()["seed-ratio"]);
		Assert.Equal("2.5", new SeedingOverride(SeedMode.Ratio, 2.5).ToOptions()["seed-ratio"]);
		Assert.Equal("0.1", new SeedingOverride(SeedMode.Ratio, 0).ToOptions()["seed-ratio"]); // 0 would mean "forever"
		Assert.Equal("0", new SeedingOverride(SeedMode.Never).ToOptions()["seed-time"]);
	}

	[Fact]
	public void Request_builds_dir_selection_seeding_and_allocation() {
		var request = new TorrentAddRequest(@"C:\dl", [1, 3], 4, new SeedingOverride(SeedMode.Never), AllocationChoice.Sparse);

		var options = request.ToOptions();

		Assert.Equal(@"C:\dl", options["dir"]);
		Assert.Equal("1,3", options["select-file"]);
		Assert.Equal("0", options["seed-time"]);
		Assert.Equal("none", options["file-allocation"]);
	}

	[Fact]
	public void Choosing_every_file_leaves_select_file_out() {
		var options = new TorrentAddRequest(@"C:\dl", [1, 2], 2).ToOptions();

		Assert.False(options.ContainsKey("select-file"));
		Assert.False(options.ContainsKey("file-allocation"));
	}

	[Fact]
	public void Duplicate_is_found_among_running_downloads_before_history() {
		var live = new DownloadItem("g1", "x", DownloadStatus.Seeding, 1, 1, 0, null, null, null, InfoHash: "abc", IsTorrent: true);
		var placeholder = new DownloadItem("g0", "x", DownloadStatus.Active, 0, 0, 0, null, null, null, InfoHash: "def", IsTorrent: true, IsMetadata: true);

		Assert.Equal("g1", TorrentOperations.FindDuplicate("ABC", [live], null)!.LiveGid);
		Assert.Null(TorrentOperations.FindDuplicate("def", [placeholder], null)); // a metadata placeholder is not "already added"
		Assert.Null(TorrentOperations.FindDuplicate("zzz", [live], null));
	}

	[Fact]
	public void Duplicate_is_found_in_history() {
		using var temp = new TempDir();
		var db = new Core.Data.Database(Path.Combine(temp.Path, "h.db"), Path.Combine(temp.Path, "b"));
		db.EnsureReady();
		var store = new HistoryStore(db);
		store.Upsert([new DownloadItem("old", "x", DownloadStatus.Complete, 1, 1, 0, null, null, null, InfoHash: "abc", IsTorrent: true)], DateTime.UtcNow);

		var duplicate = TorrentOperations.FindDuplicate("abc", [], store)!;

		Assert.Null(duplicate.LiveGid);
		Assert.Equal("old", duplicate.Past!.Gid);
	}
}

[Collection(RealAria2Collection.Name)]
public class TorrentOperationsIntegrationTests {
	private static readonly (string, int)[] TwoFiles = [("a.bin", 40_000), ("sub/b.bin", 60_000)];

	private static async Task<(TorrentOperations Ops, Core.Aria2.Aria2Engine Engine, TempDir Temp)> StartAsync() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		return (new TorrentOperations(engine, NullLogger<TorrentOperations>.Instance), engine, temp);
	}

	[RequiresAria2Fact]
	public async Task Torrent_file_with_a_file_selection_downloads_only_that_file() {
		using var swarm = new Swarm(TwoFiles);
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir, [2], 2, new SeedingOverride(SeedMode.Never)));
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[1].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "sub", "b.bin")));
		var files = await engine.Rpc!.GetFilesAsync(gid);
		Assert.False(files[0].Selected);
		Assert.True(files[1].Selected);
	}

	[RequiresAria2Fact]
	public async Task Torrent_added_paused_waits_for_Start_and_keeps_the_folder_it_was_added_with() {
		using var swarm = new Swarm(TwoFiles);
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir), paused: true);
		await Task.Delay(1500);
		var waiting = await engine.Rpc!.TellStatusAsync(gid);
		Assert.Equal(DownloadStatus.Paused, waiting.Status);
		Assert.Equal(0, waiting.CompletedBytes);

		// A different Dir here is ignored: aria2 cannot move an existing download, so only the other choices are applied.
		await ops.StartAsync(gid, new TorrentAddRequest(Path.Combine(temp.Path, "ignored"), Seeding: new SeedingOverride(SeedMode.Never)));
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
	}


	[RequiresAria2Fact]
	public async Task Magnet_resolves_to_a_file_list_then_downloads_only_the_chosen_files() {
		using var swarm = new Swarm(TwoFiles);
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;

		var metadataGid = await ops.AddMagnetAsync(swarm.Torrent.Magnet(swarm.Tracker.AnnounceUrl), swarm.LeechDir);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var resolved = await ops.WaitForMetadataAsync(metadataGid, timeout.Token);

		Assert.NotNull(resolved);
		Assert.Equal("swarm", resolved.Name);
		Assert.Equal(["swarm/a.bin", "swarm/sub/b.bin"], resolved.Files.Select(f => Path.GetRelativePath(swarm.LeechDir, f.Path).Replace('\\', '/')));
		Assert.Equal(DownloadStatus.Paused, (await engine.Rpc!.TellStatusAsync(resolved.Gid)).Status);

		await ops.StartAsync(resolved.Gid, new TorrentAddRequest(swarm.LeechDir, [1], 2, new SeedingOverride(SeedMode.Never)));
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, resolved.Gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
		Assert.True(File.Exists(Path.Combine(swarm.LeechDir, swarm.Torrent.InfoHash + ".torrent")), "aria2 should have saved the fetched metadata");
	}

	[RequiresAria2Fact]
	public async Task Magnet_that_is_removed_while_fetching_resolves_to_null() {
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;
		var lonely = TestTorrent.Create("lonely", "http://127.0.0.1:1/announce", ("x.bin", 20_000));
		var gid = await ops.AddMagnetAsync(lonely.Magnet("http://127.0.0.1:1/announce"), Path.Combine(temp.Path, "dl"));

		var waiting = ops.WaitForMetadataAsync(gid);
		await Task.Delay(700);
		Assert.False(waiting.IsCompleted);
		await ops.DiscardAsync(gid);

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Assert.Null(await waiting.WaitAsync(timeout.Token));
	}

	[RequiresAria2Fact]
	public async Task Discard_forgets_a_paused_torrent_completely() {
		using var swarm = new Swarm(TwoFiles);
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;
		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir), paused: true);

		await ops.DiscardAsync(gid);

		Assert.DoesNotContain(gid, (await engine.Rpc!.PollAsync()).Items.Select(i => i.Gid));
	}

	[RequiresAria2Fact]
	public async Task Metalink_downloads_its_file() {
		var payload = new byte[30_000];
		Random.Shared.NextBytes(payload);
		using var server = DownloadIntegrationTests.StartServer(out var prefix, async ctx => {
			ctx.Response.ContentLength64 = payload.Length;
			await ctx.Response.OutputStream.WriteAsync(payload);
			ctx.Response.Close();
		});
		var metalink = $"""
			<?xml version="1.0" encoding="UTF-8"?>
			<metalink xmlns="urn:ietf:params:xml:ns:metalink">
			  <file name="from-metalink.bin"><size>{payload.Length}</size><url>{prefix}m.bin</url></file>
			</metalink>
			""";
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;
		var dir = Path.Combine(temp.Path, "ml");

		var gids = await ops.AddMetalinkAsync(Encoding.UTF8.GetBytes(metalink), dir);

		var gid = Assert.Single(gids);
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dir, "from-metalink.bin")));
	}

	private static byte[] ReadShared(string path) {
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}

	[RequiresAria2Fact]
	public async Task Recheck_finds_a_damaged_file_and_repairs_it_from_the_swarm() {
		using var swarm = new Swarm(TwoFiles);
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;
		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir));
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status == DownloadStatus.Seeding);
		// aria2 keeps the file open, so it is damaged and read with sharing allowed.
		var damaged = Path.Combine(swarm.LeechDir, "swarm", "a.bin");
		await using (var stream = new FileStream(damaged, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
			stream.Write(new byte[2000]);
		}
		Assert.NotEqual(swarm.Torrent.Files[0].Data, ReadShared(damaged));

		await ops.RecheckAsync(gid);
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (DateTime.UtcNow < deadline && !ReadShared(damaged).AsSpan().SequenceEqual(swarm.Torrent.Files[0].Data)) {
			await Task.Delay(300);
		}

		Assert.Equal(swarm.Torrent.Files[0].Data, ReadShared(damaged));
		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Seeding or DownloadStatus.Complete);
		Assert.Equal(100_000, item.CompletedBytes);
	}

	[RequiresAria2Fact]
	public async Task Seeding_can_be_switched_off_while_the_torrent_is_seeding() {
		using var swarm = new Swarm(TwoFiles);
		var (ops, engine, temp) = await StartAsync();
		using var _ = temp;
		await using var __ = engine;
		var gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir));
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status == DownloadStatus.Seeding);

		await ops.ApplySeedingAsync(gid, new SeedingOverride(SeedMode.Never));

		var item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, item.Status);
	}
}
