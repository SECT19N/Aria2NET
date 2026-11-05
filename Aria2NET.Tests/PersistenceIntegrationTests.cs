using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

/// <summary>What aria2 keeps across a restart, checked with the real aria2c.</summary>
[Collection(RealAria2Collection.Name)]
public class PersistenceIntegrationTests {
	private static Aria2Engine NewEngine(TempDir temp) {
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		return new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance);
	}

	private static async Task<DownloadItem?> FindAsync(Aria2RpcClient rpc, string uri) {
		var snapshot = await rpc.PollAsync();
		return snapshot.Items.FirstOrDefault(i => i.FirstUri == uri);
	}

	[RequiresAria2Fact]
	public async Task A_paused_download_survives_a_graceful_restart_and_reports_its_gid() {
		using var temp = new TempDir();
		const string uri = "http://127.0.0.1:9/persist.bin";
		await using var engine = NewEngine(temp);
		await engine.StartAsync();
		var gid = await engine.Rpc!.AddUriAsync([uri], new Dictionary<string, string> { ["pause"] = "true" });

		await engine.RestartAsync();

		var restored = await FindAsync(engine.Rpc!, uri);
		Assert.NotNull(restored);
		Assert.Equal(DownloadStatus.Paused, restored.Status);
		// Recorded so the history reconcile knows whether it can match by GID.
		Assert.Equal(gid, restored.Gid);
	}

	[RequiresAria2Fact]
	public async Task A_killed_aria2_is_restarted_automatically_and_keeps_its_saved_downloads() {
		using var temp = new TempDir();
		const string uri = "http://127.0.0.1:9/crash.bin";
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		await using var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance);
		await engine.StartAsync();
		await engine.Rpc!.AddUriAsync([uri], new Dictionary<string, string> { ["pause"] = "true" });
		await engine.Rpc.SaveSessionAsync(); // aria2 saves on a 30 s timer; a crash loses what it has not saved yet

		var restarted = new TaskCompletionSource();
		using var supervisor = new EngineSupervisor(engine, new RestartPolicy(delays: [TimeSpan.FromMilliseconds(50)]), delay: Task.Delay);
		supervisor.Restarted += (_, _) => restarted.TrySetResult();
		var exe = Path.GetFullPath(paths.Aria2Exe);
		foreach (var p in System.Diagnostics.Process.GetProcessesByName("aria2c")) {
			if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
				p.Kill();
			}
		}

		await restarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(EngineState.Running, engine.State);
		var back = await FindAsync(engine.Rpc!, uri);
		Assert.NotNull(back);
		Assert.Equal(DownloadStatus.Paused, back.Status);
	}

	/// <summary>Serves <paramref name="payload"/> in small slices so a download can be interrupted half way. Understands Range.</summary>
	private static System.Net.HttpListener SlowServer(byte[] payload, out string prefix) =>
		DownloadIntegrationTests.StartServer(out prefix, async ctx => {
			var (from, to) = (0L, payload.Length - 1L);
			if (ctx.Request.Headers["Range"] is { } range && range.StartsWith("bytes=")) {
				var parts = range[6..].Split('-');
				from = long.Parse(parts[0]);
				to = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : payload.Length - 1;
				ctx.Response.StatusCode = 206;
				ctx.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{payload.Length}");
			}
			ctx.Response.ContentLength64 = to - from + 1;
			for (var pos = from; pos <= to; pos += 16 * 1024) {
				var count = (int)Math.Min(16 * 1024, to - pos + 1);
				await ctx.Response.OutputStream.WriteAsync(payload.AsMemory((int)pos, count));
				await ctx.Response.OutputStream.FlushAsync();
				await Task.Delay(40);
			}
			ctx.Response.Close();
		});

	[RequiresAria2Fact]
	public async Task Downloading_again_makes_a_new_copy_next_to_the_old_one() {
		var payload = new byte[64 * 1024];
		Random.Shared.NextBytes(payload);
		using var server = SlowServer(payload, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance);
		var dir = Path.Combine(temp.Path, "out");
		var url = prefix + "again.bin";

		var first = await engine.Rpc!.AddUriAsync([url], new Dictionary<string, string> { ["dir"] = dir });
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc, first, i => i.Status == DownloadStatus.Complete);
		var again = await ops.DownloadAgainAsync(url, dir);
		var second = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, again, i => i.Status == DownloadStatus.Complete);

		Assert.NotEqual(first, again);
		Assert.NotEqual(Path.Combine(dir, "again.bin"), second.Path);
		Assert.Equal(2, Directory.GetFiles(dir, "again*").Where(f => !f.EndsWith(".aria2")).Count());
	}

	[RequiresAria2Fact]
	public async Task A_download_cut_off_by_a_crash_leaves_files_that_are_found_and_resumed() {
		var payload = new byte[512 * 1024];
		Random.Shared.NextBytes(payload);
		using var server = SlowServer(payload, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = new DownloadOperations(engine, NullLogger<DownloadOperations>.Instance);
		var dir = Path.Combine(temp.Path, "out");
		var url = prefix + "cut.bin";

		var gid = await engine.Rpc!.AddUriAsync([url], new Dictionary<string, string> { ["dir"] = dir });
		await DownloadIntegrationTests.WaitForAsync(engine.Rpc, gid, i => i.CompletedBytes > 64 * 1024);
		var exe = Path.GetFullPath(new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!).Aria2Exe);
		foreach (var p in System.Diagnostics.Process.GetProcessesByName("aria2c")) {
			if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
				p.Kill();
				p.WaitForExit();
			}
		}
		await engine.RestartAsync();                                              // the unsaved session means aria2 starts without it

		var orphan = Assert.Single(OrphanFiles.Find([dir], (await engine.Rpc!.PollAsync()).Items));
		Assert.True(orphan.DataExists);

		var resumed = await ops.ResumeLeftoverAsync(url, dir, "cut.bin");
		var done = await DownloadIntegrationTests.WaitForAsync(engine.Rpc, resumed, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);
		Assert.Equal(DownloadStatus.Complete, done.Status);
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dir, "cut.bin")));
		Assert.Empty(OrphanFiles.Find([dir], (await engine.Rpc.PollAsync()).Items));
	}

	[RequiresAria2Fact]
	public async Task A_damaged_session_file_is_restored_from_a_backup_and_the_user_is_told() {
		using var temp = new TempDir();
		const string uri = "http://127.0.0.1:9/repair.bin";
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		await using var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance);
		await engine.StartAsync();
		await engine.Rpc!.AddUriAsync([uri], new Dictionary<string, string> { ["pause"] = "true" });
		await engine.StopAsync();            // saves the session
		await engine.StartAsync();           // healthy file: backed up before aria2 reads it
		await engine.StopAsync();

		File.WriteAllBytes(paths.Aria2SessionFile, new byte[256]);                       // what a power loss can leave behind
		EngineNotice? notice = null;
		engine.NoticeRaised += (_, n) => notice = n;
		await engine.StartAsync();

		Assert.Equal(EngineState.Running, engine.State);
		Assert.Equal(EngineNoticeKind.SessionRepaired, notice?.Kind);
		Assert.Contains("restored from a backup", notice!.Message);
		Assert.NotNull(await FindAsync(engine.Rpc!, uri));
	}
}
