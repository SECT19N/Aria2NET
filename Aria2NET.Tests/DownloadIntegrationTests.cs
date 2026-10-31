using System.Net;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

/// <summary>Downloads from a local HttpListener with the real aria2c.</summary>
[Collection(RealAria2Collection.Name)]
public class DownloadIntegrationTests {
	internal static async Task<DownloadItem> WaitForAsync(Aria2RpcClient rpc, string gid, Func<DownloadItem, bool> done) {
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (DateTime.UtcNow < deadline) {
			var item = await rpc.TellStatusAsync(gid);
			if (done(item)) {
				return item;
			}
			await Task.Delay(100);
		}
		throw new TimeoutException($"Download {gid} did not finish in time");
	}

	internal static HttpListener StartServer(out string prefix, Func<HttpListenerContext, Task> handle) {
		var listener = new HttpListener();
		while (true) {
			var port = Random.Shared.Next(20000, 60000);
			prefix = $"http://127.0.0.1:{port}/";
			listener.Prefixes.Clear();
			listener.Prefixes.Add(prefix);
			try {
				listener.Start();
				break;
			}
			catch (HttpListenerException) {
				// Port taken; try another.
			}
		}
		_ = Task.Run(async () => {
			while (listener.IsListening) {
				HttpListenerContext ctx;
				try {
					ctx = await listener.GetContextAsync();
				}
				catch (Exception) {
					return;
				}
				_ = Task.Run(async () => {
					try {
						await handle(ctx);
					}
					catch (Exception) {
						// Client went away.
					}
				});
			}
		});
		return listener;
	}

	internal static async Task<(Aria2Engine Engine, TempDir Temp)> StartEngineAsync() {
		var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance);
		await engine.StartAsync();
		return (engine, temp);
	}

	[RequiresAria2Fact]
	public async Task Downloads_a_file_to_completion_and_names_it_from_Content_Disposition() {
		var payload = new byte[5 * 1024 * 1024];
		Random.Shared.NextBytes(payload);
		using var server = StartServer(out var prefix, async ctx => {
			ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"from-header.bin\"");
			ctx.Response.ContentLength64 = payload.Length;
			await ctx.Response.OutputStream.WriteAsync(payload);
			ctx.Response.Close();
		});
		var (engine, temp) = await StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var outDir = Path.Combine(temp.Path, "out");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "download?id=7"], new Dictionary<string, string> { ["dir"] = outDir });
		var item = await WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal("from-header.bin", item.Name);
		Assert.Equal(payload.Length, item.TotalBytes);
		Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(outDir, "from-header.bin")));

		var snapshot = await engine.Rpc.PollAsync();
		Assert.Contains(snapshot.Items, i => i.Gid == gid && i.Status == DownloadStatus.Complete);
	}

	[RequiresAria2Fact]
	public async Task Honors_requested_filename() {
		using var server = StartServer(out var prefix, async ctx => {
			await ctx.Response.OutputStream.WriteAsync("hello"u8.ToArray());
			ctx.Response.Close();
		});
		var (engine, temp) = await StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;
		var outDir = Path.Combine(temp.Path, "out");

		var gid = await engine.Rpc!.AddUriAsync([prefix + "x"], new Dictionary<string, string> { ["dir"] = outDir, ["out"] = "chosen.txt" });
		var item = await WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Complete, item.Status);
		Assert.Equal("chosen.txt", item.Name);
		Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(outDir, "chosen.txt")));
	}

	[RequiresAria2Fact]
	public async Task Missing_resource_ends_in_error_with_message() {
		using var server = StartServer(out var prefix, ctx => {
			ctx.Response.StatusCode = 404;
			ctx.Response.Close();
			return Task.CompletedTask;
		});
		var (engine, temp) = await StartEngineAsync();
		using var _ = temp;
		await using var __ = engine;

		var gid = await engine.Rpc!.AddUriAsync([prefix + "nope.bin"], new Dictionary<string, string> { ["dir"] = Path.Combine(temp.Path, "out") });
		var item = await WaitForAsync(engine.Rpc, gid, i => i.Status is DownloadStatus.Complete or DownloadStatus.Error);

		Assert.Equal(DownloadStatus.Error, item.Status);
		Assert.False(string.IsNullOrWhiteSpace(item.ErrorMessage));
	}
}
