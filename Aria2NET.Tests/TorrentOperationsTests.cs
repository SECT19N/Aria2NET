using System.Net;
using System.Text;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Data;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.History;
using Aria2NET.Core.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

public class TorrentParsingTests {
	[Fact]
	public void Reads_name_hash_files_and_trackers_of_a_multi_file_torrent() {
		TestTorrent torrent = TestTorrent.Create("album", "http://tracker.example/announce", ("a.bin", 40_000), ("sub/b.bin", 60_000));

		TorrentMetadata meta = TorrentMetadata.Parse(torrent.TorrentFile);

		Assert.Equal("album", meta.Name);
		Assert.Equal(torrent.InfoHash, meta.InfoHash);
		Assert.Equal([new TorrentFileEntry(1, "a.bin", 40_000), new TorrentFileEntry(2, "sub/b.bin", 60_000)], meta.Files);
		Assert.Equal(100_000, meta.TotalBytes);
		Assert.Equal(["http://tracker.example/announce"], meta.Trackers);
	}

	[Fact]
	public void A_single_file_torrent_has_one_file_named_after_the_torrent() {
		TestTorrent torrent = TestTorrent.Create("one.iso", "http://t/a", ("one.iso", 20_000));

		TorrentMetadata meta = TorrentMetadata.Parse(torrent.TorrentFile);

		Assert.Equal([new TorrentFileEntry(1, "one.iso", 20_000)], meta.Files);
	}

	[Fact]
	public void Announce_list_tiers_are_flattened_without_duplicates() {
		byte[] file = Core.Torrents.Bencode.Encode(new Dictionary<string, object> {
			["announce"] = "http://a/x",
			["announce-list"] = new List<object> { new List<object> { "http://a/x", "udp://b:1/y" }, new List<object> { "http://c/z" } },
			["info"] = new Dictionary<string, object> { ["name"] = "n", ["length"] = 5L, ["piece length"] = 16384L, ["pieces"] = new byte[20] },
		});

		Assert.Equal(["http://a/x", "udp://b:1/y", "http://c/z"], TorrentMetadata.Parse(file).Trackers);
	}

	[Fact]
	public void Prefers_the_utf8_name_fields() {
		byte[] file = Core.Torrents.Bencode.Encode(new Dictionary<string, object> {
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
		FormatException ex = Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Encoding.Latin1.GetBytes(text)));
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
		Assert.True(MagnetLink.TryGetInfoHash(magnet, out string? hash));
		Assert.Equal(expected, hash);
	}

	[Fact]
	public void Reads_a_base32_info_hash() {
		// 20 bytes 0x00..0x13 in base32.
		byte[] bytes = Enumerable.Range(0, 20).Select(i => (byte) i).ToArray();
		string base32 = ToBase32(bytes);

		Assert.True(MagnetLink.TryGetInfoHash("magnet:?xt=urn:btih:" + base32, out string? hash));
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
		IEnumerable<int> indexes = chosen.Split(',').Select(int.Parse);

		Assert.Equal(expected, SelectFileSpec.Build(indexes, total));
	}

	[Fact]
	public void Choosing_nothing_is_an_error() {
		Assert.Throws<ArgumentException>(() => SelectFileSpec.Build([], 3));
		Assert.Throws<ArgumentException>(() => SelectFileSpec.Build([9], 3));
	}

	private static string ToBase32(byte[] bytes) {
		const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
		StringBuilder sb = new StringBuilder();
		int buffer = 0, bits = 0;
		foreach (byte b in bytes) {
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
		UrlParseResult result = UrlParser.Parse("\"C:\\My Files\\a.torrent\"\r\nhttps://a.com/x\r\nC:\\plain\\b.torrent\r\nC:\\nope\\c.txt");

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
		TorrentAddRequest request = new TorrentAddRequest(@"C:\dl", [1, 3], 4, new SeedingOverride(SeedMode.Never), AllocationChoice.Sparse);

		IReadOnlyDictionary<string, string> options = request.ToOptions();

		Assert.Equal(@"C:\dl", options["dir"]);
		Assert.Equal("1,3", options["select-file"]);
		Assert.Equal("0", options["seed-time"]);
		Assert.Equal("none", options["file-allocation"]);
	}

	[Fact]
	public void Choosing_every_file_leaves_select_file_out() {
		IReadOnlyDictionary<string, string> options = new TorrentAddRequest(@"C:\dl", [1, 2], 2).ToOptions();

		Assert.False(options.ContainsKey("select-file"));
		Assert.False(options.ContainsKey("file-allocation"));
	}

	[Fact]
	public void Duplicate_is_found_among_running_downloads_before_history() {
		DownloadItem live = new DownloadItem("g1", "x", DownloadStatus.Seeding, 1, 1, 0, null, null, null, InfoHash: "abc", IsTorrent: true);
		DownloadItem placeholder = new DownloadItem("g0", "x", DownloadStatus.Active, 0, 0, 0, null, null, null, InfoHash: "def", IsTorrent: true, IsMetadata: true);

		Assert.Equal("g1", TorrentOperations.FindDuplicate("ABC", [live], null)!.LiveGid);
		Assert.Null(TorrentOperations.FindDuplicate("def", [placeholder], null)); // a metadata placeholder is not "already added"
		Assert.Null(TorrentOperations.FindDuplicate("zzz", [live], null));
	}

	[Fact]
	public void Duplicate_is_found_in_history() {
		using TempDir temp = new TempDir();
		Database db = new Core.Data.Database(Path.Combine(temp.Path, "h.db"), Path.Combine(temp.Path, "b"));
		db.EnsureReady();
		HistoryStore store = new HistoryStore(db);
		store.Upsert([new DownloadItem("old", "x", DownloadStatus.Complete, 1, 1, 0, null, null, null, InfoHash: "abc", IsTorrent: true)], DateTime.UtcNow);

		DuplicateTorrent duplicate = TorrentOperations.FindDuplicate("abc", [], store)!;

		Assert.Null(duplicate.LiveGid);
		Assert.Equal("old", duplicate.Past!.Gid);
	}
}

[Collection(RealAria2Collection.Name)]
public class TorrentOperationsIntegrationTests {
	private static readonly (string, int)[] TwoFiles = [("a.bin", 40_000), ("sub/b.bin", 60_000)];

	private static async Task<(TorrentOperations Ops, Core.Aria2.Aria2Engine Engine, TempDir Temp)> StartAsync() {
		(Aria2Engine? engine, TempDir? temp) = await DownloadIntegrationTests.StartEngineAsync();
		return (new TorrentOperations(engine, NullLogger<TorrentOperations>.Instance), engine, temp);
	}

	[RequiresAria2Fact]
	public async Task Torrent_file_with_a_file_selection_downloads_only_that_file() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;

		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir, [2], 2, new SeedingOverride(SeedMode.Never)));
		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[1].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "sub", "b.bin")));
		IReadOnlyList<DownloadFile> files = await engine.Rpc!.GetFilesAsync(gid);
		Assert.False(files[0].Selected);
		Assert.True(files[1].Selected);
	}

	[RequiresAria2Fact]
	public async Task Torrent_added_paused_waits_for_Start_and_keeps_the_folder_it_was_added_with() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;

		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir), paused: true);
		await Task.Delay(1500);
		DownloadItem waiting = await engine.Rpc!.TellStatusAsync(gid);
		Assert.Equal(DownloadStatus.Paused, waiting.Status);
		Assert.Equal(0, waiting.CompletedBytes);

		// A different Dir here is ignored: aria2 cannot move an existing download, so only the other choices are applied.
		await ops.StartAsync(gid, new TorrentAddRequest(Path.Combine(temp.Path, "ignored"), Seeding: new SeedingOverride(SeedMode.Never)));
		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
	}


	[RequiresAria2Fact]
	public async Task Files_can_be_chosen_again_while_the_torrent_is_paused_and_it_stays_paused() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir, Seeding: new SeedingOverride(SeedMode.Never)), paused: true);

		await ops.SelectFilesAsync(gid, [2], 2);

		Assert.Equal(DownloadStatus.Paused, (await engine.Rpc!.TellStatusAsync(gid)).Status);
		IReadOnlyList<DownloadFile> files = await engine.Rpc.GetFilesAsync(gid);
		Assert.False(files[0].Selected);
		Assert.True(files[1].Selected);

		await ops.SelectFilesAsync(gid, [1, 2], 2); // everything again
		files = await engine.Rpc.GetFilesAsync(gid);
		Assert.All(files, f => Assert.True(f.Selected));

		await ops.SelectFilesAsync(gid, [1], 2);
		await engine.Rpc.UnpauseAsync([gid]);
		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
	}

	[RequiresAria2Fact]
	public async Task A_finished_torrent_refuses_a_new_choice_of_files() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir, Seeding: new SeedingOverride(SeedMode.Never)));
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SelectFilesAsync(gid, [1], 2));
		await Assert.ThrowsAsync<ArgumentException>(() => ops.SelectFilesAsync(gid, [], 2));
	}

	[RequiresAria2Fact]
	public async Task Magnet_resolves_to_a_file_list_then_downloads_only_the_chosen_files() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;

		string metadataGid = await ops.AddMagnetAsync(swarm.Torrent.Magnet(swarm.Tracker.AnnounceUrl), swarm.LeechDir);
		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		ResolvedMagnet? resolved = await ops.WaitForMetadataAsync(metadataGid, timeout.Token);

		Assert.NotNull(resolved);
		Assert.Equal("swarm", resolved.Name);
		Assert.Equal(["swarm/a.bin", "swarm/sub/b.bin"], resolved.Files.Select(f => Path.GetRelativePath(swarm.LeechDir, f.Path).Replace('\\', '/')));
		Assert.Equal(DownloadStatus.Paused, (await engine.Rpc!.TellStatusAsync(resolved.Gid)).Status);

		await ops.StartAsync(resolved.Gid, new TorrentAddRequest(swarm.LeechDir, [1], 2, new SeedingOverride(SeedMode.Never)));
		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, resolved.Gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(swarm.Torrent.Files[0].Data, await File.ReadAllBytesAsync(Path.Combine(swarm.LeechDir, "swarm", "a.bin")));
		Assert.True(File.Exists(Path.Combine(swarm.LeechDir, swarm.Torrent.InfoHash + ".torrent")), "aria2 should have saved the fetched metadata");
	}

	[RequiresAria2Fact]
	public async Task Magnet_that_is_removed_while_fetching_resolves_to_null() {
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		TestTorrent lonely = TestTorrent.Create("lonely", "http://127.0.0.1:1/announce", ("x.bin", 20_000));
		string gid = await ops.AddMagnetAsync(lonely.Magnet("http://127.0.0.1:1/announce"), Path.Combine(temp.Path, "dl"));

		Task<ResolvedMagnet?> waiting = ops.WaitForMetadataAsync(gid);
		await Task.Delay(700);
		Assert.False(waiting.IsCompleted);
		await ops.DiscardAsync(gid);

		using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Assert.Null(await waiting.WaitAsync(timeout.Token));
	}

	[RequiresAria2Fact]
	public async Task Discard_forgets_a_paused_torrent_completely() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir), paused: true);

		await ops.DiscardAsync(gid);

		Assert.DoesNotContain(gid, (await engine.Rpc!.PollAsync()).Items.Select(i => i.Gid));
	}

	[RequiresAria2Fact]
	public async Task Metalink_downloads_its_file() {
		byte[] payload = new byte[30_000];
		Random.Shared.NextBytes(payload);
		using HttpListener server = DownloadIntegrationTests.StartServer(out string? prefix, async ctx => {
			ctx.Response.ContentLength64 = payload.Length;
			await ctx.Response.OutputStream.WriteAsync(payload);
			ctx.Response.Close();
		});
		string metalink = $"""
			<?xml version="1.0" encoding="UTF-8"?>
			<metalink xmlns="urn:ietf:params:xml:ns:metalink">
			  <file name="from-metalink.bin"><size>{payload.Length}</size><url>{prefix}m.bin</url></file>
			</metalink>
			""";
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		string dir = Path.Combine(temp.Path, "ml");

		IReadOnlyList<string> gids = await ops.AddMetalinkAsync(Encoding.UTF8.GetBytes(metalink), dir);

		string gid = Assert.Single(gids);
		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dir, "from-metalink.bin")));
	}

	private static byte[] ReadShared(string path) {
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		byte[] bytes = new byte[stream.Length];
		stream.ReadExactly(bytes);
		return bytes;
	}

	[RequiresAria2Fact]
	public async Task Recheck_finds_a_damaged_file_and_repairs_it_from_the_swarm() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir));
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status == DownloadStatus.Seeding);
		// aria2 keeps the file open, so it is damaged and read with sharing allowed.
		string damaged = Path.Combine(swarm.LeechDir, "swarm", "a.bin");
		await using (FileStream stream = new FileStream(damaged, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) {
			stream.Write(new byte[2000]);
		}
		Assert.NotEqual(swarm.Torrent.Files[0].Data, ReadShared(damaged));

		await ops.RecheckAsync(gid);
		DateTime deadline = DateTime.UtcNow.AddSeconds(30);
		while (DateTime.UtcNow < deadline && !ReadShared(damaged).AsSpan().SequenceEqual(swarm.Torrent.Files[0].Data)) {
			await Task.Delay(300);
		}

		Assert.Equal(swarm.Torrent.Files[0].Data, ReadShared(damaged));
		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Seeding or DownloadStatus.Complete);
		Assert.Equal(100_000, item.CompletedBytes);
	}

	[RequiresAria2Fact]
	public async Task Seeding_can_be_switched_off_while_the_torrent_is_seeding() {
		using Swarm swarm = new Swarm(TwoFiles);
		(TorrentOperations? ops, Aria2Engine? engine, TempDir? temp) = await StartAsync();
		using TempDir _ = temp;
		await using Aria2Engine __ = engine;
		string gid = await ops.AddTorrentAsync(swarm.Torrent.TorrentFile, new TorrentAddRequest(swarm.LeechDir));
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status == DownloadStatus.Seeding);

		await ops.ApplySeedingAsync(gid, new SeedingOverride(SeedMode.Never));

		DownloadItem item = await DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, item.Status);
	}
}
