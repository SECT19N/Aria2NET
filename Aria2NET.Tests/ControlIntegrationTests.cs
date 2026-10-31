using System.Net;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Aria2NET.Tests;

/// <summary>Pause/resume/remove/retry/queue operations against the real aria2c and a slow local HTTP server.</summary>
[Collection(RealAria2Collection.Name)]
public class ControlIntegrationTests(ITestOutputHelper output) {
	private const int Chunk = 64 * 1024;

	/// <summary>Serves <paramref name="payload"/> slowly and honors Range, so pause/resume really resumes.</summary>
	private static HttpListener SlowServer(byte[] payload, int delayMs, out string prefix, Func<HttpListenerContext, bool>? intercept = null) =>
		DownloadIntegrationTests.StartServer(out prefix, async ctx => {
			if (intercept?.Invoke(ctx) == true) {
				return;
			}
			long start = 0;
			var range = ctx.Request.Headers["Range"];
			if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range[6..].Split('-')[0], out var from)) {
				start = from;
				ctx.Response.StatusCode = 206;
				ctx.Response.AddHeader("Content-Range", $"bytes {start}-{payload.Length - 1}/{payload.Length}");
			}
			ctx.Response.ContentLength64 = payload.Length - start;
			for (var pos = start; pos < payload.Length; pos += Chunk) {
				var n = (int)Math.Min(Chunk, payload.Length - pos);
				await ctx.Response.OutputStream.WriteAsync(payload.AsMemory((int)pos, n));
				await ctx.Response.OutputStream.FlushAsync();
				await Task.Delay(delayMs);
			}
			ctx.Response.Close();
		});

	private static byte[] Payload(int size) {
		var bytes = new byte[size];
		Random.Shared.NextBytes(bytes);
		return bytes;
	}

	private static DownloadOperations Ops(Aria2Engine engine) => new(engine, NullLogger<DownloadOperations>.Instance);

	private static Task<DownloadItem> Wait(Aria2Engine engine, string gid, Func<DownloadItem, bool> done) =>
		DownloadIntegrationTests.WaitForAsync(engine.Rpc!, gid, done);

	private static bool Finished(DownloadItem i) => i.Status is DownloadStatus.Complete or DownloadStatus.Error;

	[RequiresAria2Fact]
	public async Task Pause_then_resume_finishes_with_identical_bytes() {
		var payload = Payload(4 * 1024 * 1024);
		using var server = SlowServer(payload, 40, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = Ops(engine);
		var dir = Path.Combine(temp.Path, "out");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "big.bin"], new Dictionary<string, string> { ["dir"] = dir });
		var running = await Wait(engine, gid, i => i.Status == DownloadStatus.Active && i.CompletedBytes > 0);
		await ops.PauseAsync([running]);
		var paused = await Wait(engine, gid, i => i.Status == DownloadStatus.Paused);
		Assert.True(paused.CompletedBytes < payload.Length);
		Assert.True(DownloadCapabilities.CanResume(paused));

		await ops.ResumeAsync([paused]);
		var done = await Wait(engine, gid, Finished);

		Assert.True(done.Status == DownloadStatus.Complete, $"status {done.Status}, code {done.ErrorCode}: {done.ErrorMessage}");
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dir, "big.bin")));
	}

	[RequiresAria2Fact]
	public async Task Removing_an_active_download_makes_it_disappear() {
		var payload = Payload(4 * 1024 * 1024);
		using var server = SlowServer(payload, 40, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await engine.Rpc!.AddUriAsync([prefix + "big.bin"], new Dictionary<string, string> { ["dir"] = Path.Combine(temp.Path, "out") });
		var running = await Wait(engine, gid, i => i.Status == DownloadStatus.Active && i.CompletedBytes > 0);
		var problems = await Ops(engine).RemoveAsync([running], deleteFiles: false);

		Assert.Empty(problems);
		await Assert.ThrowsAsync<Aria2RpcException>(() => engine.Rpc.TellStatusAsync(gid));
		var snapshot = await engine.Rpc.PollAsync();
		Assert.DoesNotContain(snapshot.Items, i => i.Gid == gid);
	}

	[RequiresAria2Fact]
	public async Task Remove_and_delete_removes_the_partial_file_and_its_control_file() {
		var payload = Payload(4 * 1024 * 1024);
		using var server = SlowServer(payload, 40, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var dir = Path.Combine(temp.Path, "out");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "partial.bin"], new Dictionary<string, string> { ["dir"] = dir });
		var running = await Wait(engine, gid, i => i.Status == DownloadStatus.Active && i.CompletedBytes > 0);
		await Ops(engine).PauseAsync([running]);
		var paused = await Wait(engine, gid, i => i.Status == DownloadStatus.Paused);
		var file = Path.Combine(dir, "partial.bin");
		Assert.True(File.Exists(file));
		Assert.Equal(file, Path.GetFullPath(paused.Path!));

		var problems = await Ops(engine).RemoveAsync([paused], deleteFiles: true);

		Assert.Empty(problems);
		Assert.False(File.Exists(file));
		Assert.False(File.Exists(file + ".aria2"));
	}

	[RequiresAria2Fact]
	public async Task Clear_forgets_finished_downloads_but_keeps_the_file() {
		using var server = SlowServer("hi"u8.ToArray(), 0, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var dir = Path.Combine(temp.Path, "out");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "tiny.txt"], new Dictionary<string, string> { ["dir"] = dir });
		var done = await Wait(engine, gid, Finished);
		await Ops(engine).ClearAsync([done]);

		await Assert.ThrowsAsync<Aria2RpcException>(() => engine.Rpc.TellStatusAsync(gid));
		Assert.True(File.Exists(Path.Combine(dir, "tiny.txt")));
	}

	[RequiresAria2Fact]
	public async Task Retry_restarts_a_failed_download_under_a_new_gid() {
		var payload = Payload(64 * 1024);
		var failing = true;
		using var server = SlowServer(payload, 0, out var prefix, ctx => {
			if (!failing) {
				return false;
			}
			ctx.Response.StatusCode = 404;
			ctx.Response.Close();
			return true;
		});
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var dir = Path.Combine(temp.Path, "out");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "later.bin"], new Dictionary<string, string> { ["dir"] = dir });
		var failed = await Wait(engine, gid, Finished);
		Assert.Equal(DownloadStatus.Error, failed.Status);
		Assert.True(DownloadCapabilities.CanRetry(failed), "failed item must keep its address (FirstUri)");

		failing = false;
		var newGid = await Ops(engine).RetryAsync(failed);
		var done = await Wait(engine, newGid, Finished);

		Assert.NotEqual(gid, newGid);
		Assert.Equal(DownloadStatus.Complete, done.Status);
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dir, "later.bin")));
		await Assert.ThrowsAsync<Aria2RpcException>(() => engine.Rpc.TellStatusAsync(gid));
	}

	[RequiresAria2Fact]
	public async Task Queue_moves_reorder_waiting_downloads() {
		using var server = SlowServer(Payload(1024), 0, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = Ops(engine);
		var opts = new Dictionary<string, string> { ["dir"] = Path.Combine(temp.Path, "out"), ["pause"] = "true" };

		var a = await engine.Rpc!.AddUriAsync([prefix + "a"], opts);
		var b = await engine.Rpc.AddUriAsync([prefix + "b"], opts);
		var c = await engine.Rpc.AddUriAsync([prefix + "c"], opts);
		async Task<string[]> Order() => (await engine.Rpc.PollAsync()).Items.Where(i => i.Status == DownloadStatus.Paused).Select(i => i.Gid).ToArray();
		DownloadItem Item(string gid) => new(gid, gid, DownloadStatus.Paused, 0, 0, 0, null, null, null);

		Assert.Equal([a, b, c], await Order());
		await ops.MoveAsync(Item(c), QueueMove.Top);
		Assert.Equal([c, a, b], await Order());
		await ops.MoveAsync(Item(c), QueueMove.Down);
		Assert.Equal([a, c, b], await Order());
		await ops.MoveAsync(Item(b), QueueMove.Up);
		Assert.Equal([a, b, c], await Order());
		await ops.MoveAsync(Item(a), QueueMove.Bottom);
		Assert.Equal([b, c, a], await Order());
	}

	[RequiresAria2Fact]
	public async Task Paused_download_can_be_renamed_moved_and_speed_limited_before_it_starts() {
		var payload = Payload(32 * 1024);
		using var server = SlowServer(payload, 0, out var prefix);
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = Ops(engine);
		var firstDir = Path.Combine(temp.Path, "first");
		var secondDir = Path.Combine(temp.Path, "second");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "orig.bin"], new Dictionary<string, string> { ["dir"] = firstDir, ["pause"] = "true" });
		var item = await engine.Rpc.TellStatusAsync(gid);
		Assert.True(DownloadCapabilities.CanRename(item));

		await ops.RenameAsync(item, "renamed.bin");
		await ops.SetFolderAsync(item, secondDir);
		await ops.SetSpeedLimitAsync(item, 512 * 1024);
		var details = await ops.GetDetailsAsync(gid);
		Assert.Equal(512 * 1024, details.SpeedLimitBytesPerSec);

		await ops.ResumeAsync([item]);
		var done = await Wait(engine, gid, Finished);

		Assert.Equal(DownloadStatus.Complete, done.Status);
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(secondDir, "renamed.bin")));
		Assert.False(Directory.Exists(firstDir) && Directory.GetFiles(firstDir).Length > 0);
	}

	[RequiresAria2Fact]
	public async Task Rename_rejects_invalid_file_names() {
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var item = new DownloadItem("g", "n", DownloadStatus.Paused, 0, 0, 0, null, null, null);

		await Assert.ThrowsAsync<ArgumentException>(() => Ops(engine).RenameAsync(item, "a/b.txt"));
		await Assert.ThrowsAsync<ArgumentException>(() => Ops(engine).RenameAsync(item, "  "));
	}

	[RequiresAria2Fact]
	public async Task Force_start_runs_a_queued_download_beside_a_busy_slot_and_restores_the_limit() {
		var slow = Payload(4 * 1024 * 1024);
		var fast = Payload(256 * 1024);
		using var server = SlowServer(slow, 40, out var prefix, ctx => {
			if (ctx.Request.Url!.AbsolutePath != "/fast.bin") {
				return false;
			}
			ctx.Response.OutputStream.Write(fast);
			ctx.Response.Close();
			return true;
		});
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var ops = Ops(engine);
		var dir = Path.Combine(temp.Path, "out");
		await engine.Rpc!.ChangeGlobalOptionAsync(new Dictionary<string, string> { ["max-concurrent-downloads"] = "1" });

		var busy = await engine.Rpc.AddUriAsync([prefix + "slow.bin"], new Dictionary<string, string> { ["dir"] = dir });
		await Wait(engine, busy, i => i.Status == DownloadStatus.Active && i.CompletedBytes > 0);
		var queued = await engine.Rpc.AddUriAsync([prefix + "fast.bin"], new Dictionary<string, string> { ["dir"] = dir });
		var waiting = await Wait(engine, queued, i => i.Status == DownloadStatus.Waiting);
		Assert.True(DownloadCapabilities.CanForceStart(waiting));

		await ops.ForceStartAsync(waiting);
		var done = await Wait(engine, queued, Finished);
		var stillBusy = await engine.Rpc.TellStatusAsync(busy);

		Assert.Equal(DownloadStatus.Complete, done.Status);
		Assert.Equal(DownloadStatus.Active, stillBusy.Status);

		await ops.OnSnapshot(await engine.Rpc.PollAsync());
		var global = await engine.Rpc.GetGlobalOptionAsync();
		Assert.Equal("1", global["max-concurrent-downloads"]);
	}

	[RequiresAria2Fact]
	public async Task Reports_what_aria2_knows_about_a_redirected_download() {
		var payload = Payload(16 * 1024);
		using var server = SlowServer(payload, 0, out var prefix, ctx => {
			if (ctx.Request.Url!.AbsolutePath != "/start") {
				return false;
			}
			ctx.Response.StatusCode = 302;
			ctx.Response.AddHeader("Location", "/final.bin");
			ctx.Response.Close();
			return true;
		});
		var (engine, temp) = await DownloadIntegrationTests.StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await engine.Rpc!.AddUriAsync([prefix + "start"], new Dictionary<string, string> { ["dir"] = Path.Combine(temp.Path, "out"), ["referer"] = "https://ref.example/" });
		await Wait(engine, gid, Finished);
		var details = await Ops(engine).GetDetailsAsync(gid);

		foreach (var (uri, status) in details.Uris) {
			output.WriteLine($"uri: {uri} ({status})");
		}
		output.WriteLine($"name: {details.Item.Name}, path: {details.Item.Path}");
		Assert.Equal("https://ref.example/", details.Referer);
		Assert.Contains(details.Uris, u => u.Uri == prefix + "start");
		Assert.Equal(DownloadStatus.Complete, details.Item.Status);
	}
}
