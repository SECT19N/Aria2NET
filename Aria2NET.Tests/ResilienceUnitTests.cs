using Aria2NET.Core;
using Aria2NET.Core.Aria2;
using Aria2NET.Core.Downloads;

namespace Aria2NET.Tests;

public class SessionFileTests {
	private static AppPaths Paths(TempDir temp) {
		var paths = new AppPaths(Path.Combine(temp.Path, "data"), Path.Combine(temp.Path, "tools"));
		paths.EnsureDirectories();
		return paths;
	}

	private static string[] Backups(AppPaths paths) => Directory.GetFiles(paths.BackupsDir, SessionFile.BackupPrefix + "-*");

	[Fact]
	public void Check_tells_the_four_states_apart() {
		using var temp = new TempDir();
		var file = Path.Combine(temp.Path, "s");

		Assert.Equal(SessionHealth.Missing, SessionFile.Check(file));
		File.WriteAllBytes(file, []);
		Assert.Equal(SessionHealth.Empty, SessionFile.Check(file));
		File.WriteAllText(file, "http://h/a.zip\n dir=C:\\dl\n");
		Assert.Equal(SessionHealth.Ok, SessionFile.Check(file));
		File.WriteAllBytes(file, new byte[64]);
		Assert.Equal(SessionHealth.Corrupt, SessionFile.Check(file));
		File.WriteAllBytes(file, [0x68, 0x74, 0xC3, 0x28, 0x70]);
		Assert.Equal(SessionHealth.Corrupt, SessionFile.Check(file));
	}

	[Fact]
	public void A_missing_file_is_created_empty() {
		using var temp = new TempDir();
		var paths = Paths(temp);

		var result = SessionFile.PrepareForStart(paths);

		Assert.False(result.Repaired);
		Assert.Equal(0, new FileInfo(paths.Aria2SessionFile).Length);
		Assert.Empty(Backups(paths));
	}

	[Fact]
	public void A_healthy_file_is_backed_up_but_an_unchanged_one_is_not_backed_up_twice() {
		using var temp = new TempDir();
		var paths = Paths(temp);
		File.WriteAllText(paths.Aria2SessionFile, "http://h/a.zip\n");

		SessionFile.PrepareForStart(paths);
		SessionFile.PrepareForStart(paths);
		Assert.Single(Backups(paths));

		File.WriteAllText(paths.Aria2SessionFile, "http://h/a.zip\nhttp://h/b.zip\n");
		SessionFile.PrepareForStart(paths);
		Assert.Equal(2, Backups(paths).Length);
	}

	[Fact]
	public void Only_the_newest_backups_are_kept() {
		using var temp = new TempDir();
		var paths = Paths(temp);
		for (var i = 0; i < SessionFile.KeepBackups + 3; i++) {
			File.WriteAllText(paths.Aria2SessionFile, $"http://h/{i}.zip\n");
			SessionFile.PrepareForStart(paths);
		}
		Assert.Equal(SessionFile.KeepBackups, Backups(paths).Length);
	}

	[Fact]
	public void A_damaged_file_is_replaced_by_the_newest_healthy_backup() {
		using var temp = new TempDir();
		var paths = Paths(temp);
		File.WriteAllText(paths.Aria2SessionFile, "http://h/old.zip\n");
		SessionFile.PrepareForStart(paths);
		File.WriteAllText(paths.Aria2SessionFile, "http://h/new.zip\n");
		SessionFile.PrepareForStart(paths);
		File.WriteAllBytes(paths.Aria2SessionFile, new byte[128]);

		var result = SessionFile.PrepareForStart(paths);

		Assert.True(result.Repaired);
		Assert.NotNull(result.RestoredFrom);
		Assert.Equal("http://h/new.zip\n", File.ReadAllText(paths.Aria2SessionFile).Replace("\r\n", "\n"));
		Assert.True(File.Exists(result.MovedTo));
	}

	[Fact]
	public void A_damaged_backup_is_skipped_and_with_no_usable_backup_the_file_starts_empty() {
		using var temp = new TempDir();
		var paths = Paths(temp);
		File.WriteAllBytes(paths.Aria2SessionFile, new byte[16]);

		var result = SessionFile.PrepareForStart(paths);

		Assert.True(result.Repaired);
		Assert.Null(result.RestoredFrom);
		Assert.Equal(0, new FileInfo(paths.Aria2SessionFile).Length);
	}
}

public class RestartPolicyTests {
	private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

	[Fact]
	public void Delays_grow_and_the_policy_gives_up_after_the_limit() {
		var policy = new RestartPolicy(maxRestarts: 3);

		Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(T0));
		Assert.Equal(TimeSpan.FromSeconds(3), policy.NextDelay(T0.AddSeconds(5)));
		Assert.Equal(TimeSpan.FromSeconds(10), policy.NextDelay(T0.AddSeconds(20)));
		Assert.Null(policy.NextDelay(T0.AddSeconds(60)));
	}

	[Fact]
	public void Old_crashes_stop_counting_after_the_window() {
		var policy = new RestartPolicy(maxRestarts: 2, window: TimeSpan.FromMinutes(5));
		policy.NextDelay(T0);
		policy.NextDelay(T0.AddSeconds(10));
		Assert.Null(policy.NextDelay(T0.AddSeconds(20)));

		Assert.Equal(TimeSpan.FromSeconds(1), policy.NextDelay(T0.AddMinutes(6)));
	}

	[Fact]
	public void Reset_forgets_earlier_crashes() {
		var policy = new RestartPolicy(maxRestarts: 1);
		policy.NextDelay(T0);
		Assert.Null(policy.NextDelay(T0));
		policy.Reset();
		Assert.NotNull(policy.NextDelay(T0));
	}
}

public class EngineSupervisorTests {
	private sealed class FakeEngine : IAria2Engine {
		public EngineState State { get; set; } = EngineState.Running;
		public string? LastError { get; set; } = "boom";
		public Aria2Version? Version => null;
		public Aria2RpcClient? Rpc => null;
		public int Restarts;
		/// <summary>State after each restart attempt; the last entry repeats.</summary>
		public List<EngineState> Outcomes { get; } = [EngineState.Running];

		public event EventHandler? StateChanged;
		public event EventHandler<EngineNotice>? NoticeRaised { add { } remove { } }

		public void Crash() {
			State = EngineState.Crashed;
			StateChanged?.Invoke(this, EventArgs.Empty);
		}

		public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
		public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

		public Task RestartAsync(CancellationToken ct = default) {
			State = Outcomes[Math.Min(Restarts, Outcomes.Count - 1)];
			Restarts++;
			StateChanged?.Invoke(this, EventArgs.Empty);
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private static EngineSupervisor Create(FakeEngine engine, RestartPolicy? policy = null, TaskCompletionSource? waited = null) =>
		new(engine, policy ?? new RestartPolicy(), delay: (_, _) => { waited?.TrySetResult(); return Task.CompletedTask; });

	[Fact]
	public async Task A_crash_restarts_the_engine_and_reports_it() {
		var engine = new FakeEngine();
		var restarted = new TaskCompletionSource();
		using var supervisor = Create(engine);
		supervisor.Restarted += (_, _) => restarted.TrySetResult();

		engine.Crash();

		await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(1, engine.Restarts);
		Assert.Equal(EngineState.Running, engine.State);
	}

	[Fact]
	public async Task Repeated_failures_end_with_GaveUp_and_a_crashed_engine() {
		var engine = new FakeEngine { Outcomes = { [0] = EngineState.Crashed } };
		var gaveUp = new TaskCompletionSource();
		using var supervisor = Create(engine, new RestartPolicy(maxRestarts: 2));
		supervisor.GaveUp += (_, _) => gaveUp.TrySetResult();

		engine.Crash();

		await gaveUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(2, engine.Restarts);
		Assert.Equal(EngineState.Crashed, engine.State);
	}

	[Fact]
	public async Task It_does_not_restart_an_engine_the_user_already_restarted() {
		var engine = new FakeEngine();
		var release = new TaskCompletionSource();
		var delayStarted = new TaskCompletionSource();
		using var supervisor = new EngineSupervisor(engine, new RestartPolicy(), delay: async (_, _) => {
			delayStarted.TrySetResult();
			await release.Task;
		});

		engine.Crash();
		await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		engine.State = EngineState.Running; // the user pressed "Restart engine"
		release.SetResult();
		await Task.Delay(100);

		Assert.Equal(0, engine.Restarts);
	}

	[Fact]
	public async Task Stopping_the_engine_on_purpose_is_not_a_crash() {
		var engine = new FakeEngine();
		using var supervisor = Create(engine);

		engine.State = EngineState.Stopped;
		await Task.Delay(100);

		Assert.Equal(0, engine.Restarts);
	}
}

public class OrphanFilesTests {
	private static DownloadItem Live(string path) =>
		new("g", Path.GetFileName(path), DownloadStatus.Active, 1, 0, 0, null, Path.GetDirectoryName(path), "http://h/x", path);

	[Fact]
	public void Finds_control_files_that_no_live_download_owns_and_reports_the_partial_data() {
		using var temp = new TempDir();
		var owned = Path.Combine(temp.Path, "busy.zip");
		var stray = Path.Combine(temp.Path, "stray.zip");
		var strayNoData = Path.Combine(temp.Path, "gone.zip");
		File.WriteAllText(owned, "x");
		File.WriteAllText(owned + ".aria2", "c");
		File.WriteAllText(stray, "12345");
		File.WriteAllText(stray + ".aria2", "c");
		File.WriteAllText(strayNoData + ".aria2", "c");
		File.WriteAllText(Path.Combine(temp.Path, "notes.txt"), "unrelated");

		var orphans = OrphanFiles.Find([temp.Path, temp.Path.ToUpperInvariant(), Path.Combine(temp.Path, "missing")], [Live(owned)]);

		Assert.Equal(2, orphans.Count);
		var withData = orphans.Single(o => o.DataFile == stray);
		Assert.True(withData.DataExists);
		Assert.Equal(5, withData.DataBytes);
		Assert.False(orphans.Single(o => o.DataFile == strayNoData).DataExists);
	}

	[Fact]
	public void Clean_removes_the_control_file_and_only_removes_the_data_when_asked() {
		using var temp = new TempDir();
		var data = Path.Combine(temp.Path, "a.zip");
		File.WriteAllText(data, "partial");
		File.WriteAllText(data + ".aria2", "c");
		var orphan = OrphanFiles.Find([temp.Path], []).Single();

		Assert.Equal(1, OrphanFiles.Clean([orphan], deleteData: false));
		Assert.False(File.Exists(data + ".aria2"));
		Assert.True(File.Exists(data));

		File.WriteAllText(data + ".aria2", "c");
		Assert.Equal(1, OrphanFiles.Clean(OrphanFiles.Find([temp.Path], []), deleteData: true));
		Assert.False(File.Exists(data));
	}
}
