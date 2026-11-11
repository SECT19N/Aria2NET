namespace Aria2NET.Core.Data;

/// <summary>
/// Held for the life of the app so two copies (or two Windows sessions of the same user) cannot write one data folder at once.
/// The OS releases the file lock if the process dies, so a crash never leaves the folder locked.
/// </summary>
public sealed class DataFolderLock : IDisposable {
	private readonly FileStream _stream;

	private DataFolderLock(FileStream stream) => _stream = stream;

	/// <summary>Returns null when another process already holds the lock.</summary>
	public static DataFolderLock? TryAcquire(string lockFile) {
		Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
		try {
			var stream = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			return new DataFolderLock(stream);
		} catch (IOException) {
			return null;
		}
	}

	public void Dispose() => _stream.Dispose();
}
