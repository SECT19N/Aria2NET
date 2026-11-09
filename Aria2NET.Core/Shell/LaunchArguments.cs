using System.Text;
using Aria2NET.Core.Downloads;

namespace Aria2NET.Core.Shell;

/// <summary>What Windows hands the app when the user opens a .torrent/.metalink file or a magnet link with it.</summary>
public static class LaunchArguments {
	/// <summary>The links and files among the command-line arguments, in order. Other arguments (and files that are not torrents) are ignored.</summary>
	/// <param name="baseDirectory">Relative file names are resolved against this (the working directory by default).</param>
	public static IReadOnlyList<string> Extract(IEnumerable<string> args, string? baseDirectory = null) {
		var result = new List<string>();
		foreach (var raw in args) {
			var arg = raw.Trim();
			if (arg.Length == 0 || arg.StartsWith('-') || arg.StartsWith('/') && !Path.IsPathFullyQualified(arg)) {
				continue;
			}
			var token = arg;
			if (!arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && !Path.IsPathFullyQualified(arg)) {
				try {
					token = Path.GetFullPath(arg, baseDirectory ?? Directory.GetCurrentDirectory());
				}
				catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
					continue;
				}
			}
			if (UrlParser.Classify(token) is LinkKind.Magnet or LinkKind.TorrentFile or LinkKind.MetalinkFile && !result.Contains(token, StringComparer.Ordinal)) {
				result.Add(token);
			}
		}
		return result;
	}

	/// <summary>Passed by the Run key entry so the app can tell a start at sign-in from a launch by the user.</summary>
	public const string AutostartArgument = "--autostart";

	/// <summary>True when Windows started the app at sign-in.</summary>
	public static bool IsAutostart(IEnumerable<string> args) =>
		args.Any(a => string.Equals(a.Trim(), AutostartArgument, StringComparison.OrdinalIgnoreCase));

	/// <summary>Splits a command line into arguments the way Windows does (spaces separate, double quotes group, backslashes before a quote escape it).</summary>
	public static IReadOnlyList<string> Split(string? commandLine) {
		var args = new List<string>();
		if (string.IsNullOrWhiteSpace(commandLine)) {
			return args;
		}
		var current = new StringBuilder();
		var inQuotes = false;
		var started = false;
		for (var i = 0; i < commandLine.Length; i++) {
			var c = commandLine[i];
			if (c == '\\') {
				var slashes = 0;
				while (i < commandLine.Length && commandLine[i] == '\\') {
					slashes++;
					i++;
				}
				if (i < commandLine.Length && commandLine[i] == '"') {
					current.Append('\\', slashes / 2);
					if (slashes % 2 == 1) {
						current.Append('"');
					}
					else {
						inQuotes = !inQuotes;
					}
				}
				else {
					current.Append('\\', slashes);
					i--;
				}
				started = true;
			}
			else if (c == '"') {
				inQuotes = !inQuotes;
				started = true;
			}
			else if (char.IsWhiteSpace(c) && !inQuotes) {
				if (started) {
					args.Add(current.ToString());
					current.Clear();
					started = false;
				}
			}
			else {
				current.Append(c);
				started = true;
			}
		}
		if (started) {
			args.Add(current.ToString());
		}
		return args;
	}
}
