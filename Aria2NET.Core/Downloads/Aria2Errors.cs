namespace Aria2NET.Core.Downloads;

/// <summary>Plain-language text for aria2's exit/error codes.</summary>
public static class Aria2Errors {
	private static readonly Dictionary<int, string> Messages = new() {
		[1] = "Unknown error",
		[2] = "The server took too long to respond",
		[3] = "File not found on the server",
		[4] = "File not found (too many \"not found\" replies)",
		[5] = "The download was too slow and was stopped",
		[6] = "Network problem",
		[7] = "The download was not finished when aria2 stopped",
		[8] = "The server does not support resuming",
		[9] = "Not enough disk space",
		[10] = "Piece length differs from the existing control file",
		[11] = "The same file is already being downloaded",
		[12] = "The same torrent is already being downloaded",
		[13] = "The file already exists",
		[14] = "Could not rename the file",
		[15] = "Could not open the existing file",
		[16] = "Could not create or truncate the file",
		[17] = "Disk read/write error",
		[18] = "Could not create the folder",
		[19] = "Could not resolve the host name",
		[20] = "Could not parse the metalink document",
		[21] = "FTP command failed",
		[22] = "The server sent an unexpected reply",
		[23] = "Too many redirects",
		[24] = "Authorization failed",
		[25] = "Could not parse the torrent data",
		[26] = "The .torrent file is corrupted or missing information",
		[27] = "The magnet link is invalid",
		[28] = "Invalid option",
		[29] = "The server is overloaded or down for maintenance",
		[30] = "Could not parse the JSON-RPC request",
	};

	/// <summary>Friendly text for <paramref name="code"/>; falls back to aria2's own message, then a generic text.</summary>
	public static string Describe(int? code, string? rawMessage) {
		if (code is { } c && Messages.TryGetValue(c, out var friendly)) {
			return friendly;
		}
		return string.IsNullOrWhiteSpace(rawMessage) ? "The download failed" : rawMessage;
	}
}
