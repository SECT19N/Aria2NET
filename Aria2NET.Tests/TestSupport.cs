namespace Aria2NET.Tests;

internal sealed class TempDir : IDisposable {
	public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "a2n-test-" + Guid.NewGuid().ToString("N"));

	public TempDir() => Directory.CreateDirectory(Path);

	public void Dispose() {
		try {
			Directory.Delete(Path, recursive: true);
		}
		catch (IOException) {
			// Best effort cleanup.
		}
	}
}

internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler {
	public List<string> Bodies { get; } = [];

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
		Bodies.Add(body);
		return respond(request, body);
	}
}

/// <summary>Fact that is skipped when the bundled aria2c.exe has not been fetched.</summary>
internal sealed class RequiresAria2Fact : FactAttribute {
	public static string? FindAria2Dir() {
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null) {
			var candidate = System.IO.Path.Combine(dir.FullName, "tools", "aria2", "aria2c.exe");
			if (File.Exists(candidate)) {
				return System.IO.Path.GetDirectoryName(candidate);
			}
			dir = dir.Parent;
		}
		return null;
	}

	public RequiresAria2Fact() {
		if (FindAria2Dir() is null) {
			Skip = "tools/aria2/aria2c.exe missing; run tools/get-aria2.ps1";
		}
	}
}

/// <summary>Integration tests share the one aria2c.exe path and count its processes, so they must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealAria2Collection {
	public const string Name = "real-aria2";
}
