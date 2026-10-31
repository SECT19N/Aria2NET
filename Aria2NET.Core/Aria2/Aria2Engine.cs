using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Core.Aria2;

/// <summary>Owns a hidden aria2c child process and the RPC connection to it.</summary>
public sealed class Aria2Engine(AppPaths paths, string downloadDir, ILogger<Aria2Engine> logger) : IAria2Engine {
	private const int MaxStartAttempts = 3;
	private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

	private readonly AppPaths _paths = paths;
	private readonly string _downloadDir = downloadDir;
	private readonly ILogger<Aria2Engine> _logger = logger;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

	private Process? _process;
	private bool _stopping;

	public EngineState State { get; private set; } = EngineState.Stopped;
	public string? LastError { get; private set; }
	public Aria2Version? Version { get; private set; }
	public Aria2RpcClient? Rpc { get; private set; }

	public event EventHandler? StateChanged;

	public async Task StartAsync(CancellationToken ct = default) {
		await _gate.WaitAsync(ct);
		try {
			if (State is EngineState.Running) {
				return;
			}
			SetState(EngineState.Starting);
			await StartCoreAsync(ct);
		}
		finally {
			_gate.Release();
		}
	}

	public async Task StopAsync(CancellationToken ct = default) {
		await _gate.WaitAsync(ct);
		try {
			SetState(EngineState.Stopping);
			await StopCoreAsync();
			SetState(EngineState.Stopped);
		}
		finally {
			_gate.Release();
		}
	}

	public async Task RestartAsync(CancellationToken ct = default) {
		await _gate.WaitAsync(ct);
		try {
			SetState(EngineState.Restarting);
			await StopCoreAsync();
			await StartCoreAsync(ct);
		}
		finally {
			_gate.Release();
		}
	}

	public async ValueTask DisposeAsync() {
		await StopAsync();
		_http.Dispose();
		_gate.Dispose();
	}

	private async Task StartCoreAsync(CancellationToken ct) {
		LastError = null;
		var exe = Path.GetFullPath(_paths.Aria2Exe);
		if (!File.Exists(exe)) {
			Fail($"aria2c.exe not found at {exe}. Run tools/get-aria2.ps1.");
			return;
		}

		try {
			_paths.EnsureDirectories();
			Directory.CreateDirectory(_downloadDir);
			// aria2 warns about a missing --input-file, so make sure one exists.
			if (!File.Exists(_paths.Aria2SessionFile)) {
				File.WriteAllBytes(_paths.Aria2SessionFile, []);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Fail($"Cannot prepare data folders: {ex.Message}");
			return;
		}

		KillOrphans(exe);

		for (var attempt = 1; attempt <= MaxStartAttempts; attempt++) {
			ct.ThrowIfCancellationRequested();
			var config = Aria2Config.CreateRandom(_downloadDir, Environment.ProcessId);
			File.WriteAllText(_paths.Aria2ConfFile, config.Render(_paths));

			Process process;
			try {
				process = LaunchProcess(exe);
			}
			catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
				Fail($"Could not launch aria2c: {ex.Message}");
				return;
			}

			var rpc = new Aria2RpcClient(_http, new Uri($"http://127.0.0.1:{config.Port}/jsonrpc"), config.Secret);
			var version = await WaitUntilReadyAsync(process, rpc, ct);
			if (version is not null) {
				_process = process;
				_stopping = false;
				Rpc = rpc;
				Version = version;
				process.Exited += OnProcessExited;
				// Exited may have fired between readiness and subscribing.
				if (process.HasExited) {
					OnProcessExited(process, EventArgs.Empty);
					return;
				}
				_logger.LogInformation("aria2 {Version} running on port {Port} (pid {Pid})", version.Version, config.Port, process.Id);
				SetState(EngineState.Running);
				return;
			}

			_logger.LogWarning("aria2c start attempt {Attempt}/{Max} failed; retrying with a new port", attempt, MaxStartAttempts);
			KillQuietly(process);
			process.Dispose();
		}

		Fail("aria2c started but never answered on its RPC port.");
	}

	private Process LaunchProcess(string exe) {
		var psi = new ProcessStartInfo(exe) {
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = _paths.EngineDir,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("--conf-path=" + _paths.Aria2ConfFile);

		var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		process.OutputDataReceived += (_, e) => LogProcessLine(e.Data);
		process.ErrorDataReceived += (_, e) => LogProcessLine(e.Data);
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return process;
	}

	private void LogProcessLine(string? line) {
		if (!string.IsNullOrWhiteSpace(line)) {
			_logger.LogInformation("aria2c: {Line}", line);
		}
	}

	private static async Task<Aria2Version?> WaitUntilReadyAsync(Process process, Aria2RpcClient rpc, CancellationToken ct) {
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(ReadyTimeout);
		while (!process.HasExited) {
			try {
				return await rpc.GetVersionAsync(timeout.Token);
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Aria2RpcException) {
				if (timeout.IsCancellationRequested) {
					ct.ThrowIfCancellationRequested();
					return null;
				}
			}
			try {
				await Task.Delay(150, timeout.Token);
			}
			catch (OperationCanceledException) {
				ct.ThrowIfCancellationRequested();
				return null;
			}
		}
		return null;
	}

	private async Task StopCoreAsync() {
		var process = _process;
		var rpc = Rpc;
		_process = null;
		Rpc = null;
		Version = null;
		if (process is null) {
			return;
		}

		_stopping = true;
		process.Exited -= OnProcessExited;
		try {
			if (!process.HasExited) {
				if (rpc is not null) {
					try {
						using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
						await rpc.SaveSessionAsync(cts.Token);
						await rpc.ShutdownAsync(cts.Token);
					}
					catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Aria2RpcException) {
						_logger.LogWarning(ex, "Graceful aria2 shutdown failed; killing the process");
					}
				}
				try {
					using var cts = new CancellationTokenSource(ExitTimeout);
					await process.WaitForExitAsync(cts.Token);
				}
				catch (OperationCanceledException) {
					KillQuietly(process);
				}
			}
			_logger.LogInformation("aria2 stopped");
		}
		finally {
			process.Dispose();
		}
	}

	private void OnProcessExited(object? sender, EventArgs e) {
		if (_stopping || sender is not Process process || !ReferenceEquals(process, _process)) {
			return;
		}
		int code;
		try {
			code = process.ExitCode;
		}
		catch (InvalidOperationException) {
			code = -1;
		}
		_logger.LogError("aria2c exited unexpectedly with code {Code}", code);
		Rpc = null;
		Version = null;
		LastError = $"aria2c exited unexpectedly (code {code}).";
		SetState(EngineState.Crashed);
	}

	private void KillOrphans(string exe) {
		foreach (var p in Process.GetProcessesByName("aria2c")) {
			try {
				if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
					_logger.LogWarning("Killing orphaned aria2c (pid {Pid})", p.Id);
					p.Kill(entireProcessTree: true);
					p.WaitForExit(2000);
				}
			}
			catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) {
				// Process exited or is not ours to inspect.
			}
			finally {
				p.Dispose();
			}
		}
	}

	private static void KillQuietly(Process process) {
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
				process.WaitForExit(2000);
			}
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) {
			// Already gone.
		}
	}

	private void Fail(string message) {
		_logger.LogError("Engine failed: {Message}", message);
		LastError = message;
		SetState(EngineState.Failed);
	}

	private void SetState(EngineState state) {
		State = state;
		StateChanged?.Invoke(this, EventArgs.Empty);
	}
}
