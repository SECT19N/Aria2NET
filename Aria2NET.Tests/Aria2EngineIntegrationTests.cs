using System.Diagnostics;
using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aria2NET.Tests;

[Collection(RealAria2Collection.Name)]
public class Aria2EngineIntegrationTests {
	private static (Aria2Engine Engine, AppPaths Paths, TempDir Temp) Create() {
		var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), RequiresAria2Fact.FindAria2Dir()!);
		return (new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance), paths, temp);
	}

	private static List<Process> OurProcesses(AppPaths paths) {
		var exe = Path.GetFullPath(paths.Aria2Exe);
		var result = new List<Process>();
		foreach (var p in Process.GetProcessesByName("aria2c")) {
			try {
				if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
					result.Add(p);
					continue;
				}
			}
			catch (System.ComponentModel.Win32Exception) {
				// Not inspectable.
			}
			p.Dispose();
		}
		return result;
	}

	[RequiresAria2Fact]
	public async Task Start_reports_version_and_Stop_removes_process() {
		var (engine, paths, temp) = Create();
		using var _ = temp;
		await engine.StartAsync();
		Assert.Equal(EngineState.Running, engine.State);
		Assert.Equal("1.37.0", engine.Version?.Version);
		Assert.NotEmpty(OurProcesses(paths));

		await engine.StopAsync();
		Assert.Equal(EngineState.Stopped, engine.State);
		Assert.Null(engine.Rpc);
		Assert.Empty(OurProcesses(paths));
		await engine.DisposeAsync();
	}

	[RequiresAria2Fact]
	public async Task Restart_gives_a_working_engine() {
		var (engine, _, temp) = Create();
		using var _ = temp;
		await engine.StartAsync();
		await engine.RestartAsync();
		Assert.Equal(EngineState.Running, engine.State);
		Assert.NotNull(await engine.Rpc!.GetVersionAsync());
		await engine.DisposeAsync();
	}

	[RequiresAria2Fact]
	public async Task Killed_process_is_reported_as_Crashed() {
		var (engine, paths, temp) = Create();
		using var _ = temp;
		await engine.StartAsync();
		var crashed = new TaskCompletionSource();
		engine.StateChanged += (_, _) => {
			if (engine.State == EngineState.Crashed) {
				crashed.TrySetResult();
			}
		};

		foreach (var p in OurProcesses(paths)) {
			p.Kill();
		}
		await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.NotNull(engine.LastError);
		await engine.DisposeAsync();
	}

	[RequiresAria2Fact]
	public async Task Orphaned_process_is_killed_on_start() {
		var (engine, paths, temp) = Create();
		using var _ = temp;
		var orphan = Process.Start(new ProcessStartInfo(paths.Aria2Exe, "--enable-rpc --rpc-listen-port=0") { CreateNoWindow = true, UseShellExecute = false })!;
		await Task.Delay(300);
		await engine.StartAsync();
		Assert.True(orphan.HasExited);
		await engine.DisposeAsync();
	}

	[Fact]
	public async Task Missing_binary_gives_Failed_state() {
		using var temp = new TempDir();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), Path.Combine(temp.Path, "nope"));
		var engine = new Aria2Engine(paths, Path.Combine(temp.Path, "dl"), NullLogger<Aria2Engine>.Instance);
		await engine.StartAsync();
		Assert.Equal(EngineState.Failed, engine.State);
		Assert.Contains("aria2c.exe not found", engine.LastError);
		await engine.DisposeAsync();
	}
}
