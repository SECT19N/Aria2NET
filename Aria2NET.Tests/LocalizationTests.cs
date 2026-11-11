using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aria2NET.Tests;

/// <summary>
/// Keeps the three places a UI string lives in step: the English default in the XAML or in <c>Loc.S/F(key, english)</c>, and
/// <c>Strings\en-US\Resources.resw</c>. A key missing from the .resw would show English (the fallback), but a text that differs would show
/// different words depending on whether the resource loads, so any difference fails here.
/// </summary>
public partial class LocalizationTests {
	private static readonly string AppDir = FindAppDir();

	private static string FindAppDir() {
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aria2NET.slnx"))) {
			dir = dir.Parent;
		}
		Assert.NotNull(dir);
		return Path.Combine(dir.FullName, "Aria2NET");
	}

	private static Dictionary<string, string> Resw() {
		var doc = XDocument.Load(Path.Combine(AppDir, "Strings", "en-US", "Resources.resw"));
		var names = doc.Root!.Elements("data").Select(d => (string) d.Attribute("name")!).ToList();
		Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count()); // no key twice
		return doc.Root.Elements("data").ToDictionary(d => (string) d.Attribute("name")!, d => d.Element("value")!.Value);
	}

	private static IEnumerable<string> Files(string pattern) =>
		Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

	[GeneratedRegex(@"<(?<tag>[A-Za-z]\w*)(?<attrs>(?:\s+[\w:.]+\s*=\s*""[^""]*"")+)\s*/?>")]
	private static partial Regex XamlTag();

	[GeneratedRegex(@"(?<key>[\w:.]+)\s*=\s*""(?<val>[^""]*)""")]
	private static partial Regex Attr();

	[GeneratedRegex(@"Loc\.[SF]\(\s*""(?<key>[A-Za-z0-9_]+)""\s*,\s*""(?<text>(?:[^""\\]|\\.)*)""")]
	private static partial Regex LocCall();

	private static string Unescape(string text) {
		var sb = new StringBuilder();
		for (var i = 0; i < text.Length; i++) {
			if (text[i] != '\\' || i + 1 >= text.Length) {
				sb.Append(text[i]);
				continue;
			}
			i++;
			sb.Append(text[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => text[i] });
		}
		return sb.ToString();
	}

	/// <summary>Every x:Uid in the XAML with its literal attributes (the English defaults).</summary>
	private static List<(string File, string Uid, Dictionary<string, string> Attributes)> XamlUids() {
		var found = new List<(string, string, Dictionary<string, string>)>();
		foreach (var file in Files("*.xaml")) {
			foreach (Match tag in XamlTag().Matches(File.ReadAllText(file))) {
				var attrs = Attr().Matches(tag.Groups["attrs"].Value).ToDictionary(m => m.Groups["key"].Value, m => WebUtility.HtmlDecode(m.Groups["val"].Value));
				if (attrs.TryGetValue("x:Uid", out var uid)) {
					found.Add((Path.GetFileName(file), uid, attrs));
				}
			}
		}
		return found;
	}

	[Fact]
	public void Every_xaml_uid_has_resource_entries_and_every_entry_belongs_to_a_uid() {
		var resw = Resw();
		var uids = XamlUids();
		Assert.NotEmpty(uids);

		Assert.Equal(uids.Count, uids.Select(u => u.Uid).Distinct().Count()); // an x:Uid used twice would share one set of strings
		foreach (var (file, uid, _) in uids) {
			Assert.True(resw.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)), $"{file}: x:Uid \"{uid}\" has no entry in Resources.resw");
		}

		var known = uids.Select(u => u.Uid).ToHashSet(StringComparer.Ordinal);
		foreach (var key in resw.Keys.Where(k => k.Contains('.'))) {
			Assert.True(known.Contains(key[..key.IndexOf('.')]), $"Resources.resw entry \"{key}\" matches no x:Uid in any XAML file");
		}
	}

	[Fact]
	public void No_xaml_uid_sits_on_a_window_because_it_is_not_a_dependency_object() {
		// A Window cannot take resource properties: loading the XAML fails with "Failed to assign to property 'Window.Title'".
		foreach (var file in Files("*.xaml")) {
			Assert.DoesNotMatch(@"<Window\b[^>]*x:Uid", File.ReadAllText(file));
		}
	}

	[Fact]
	public void Xaml_defaults_equal_the_resource_text() {
		var resw = Resw();
		foreach (var (file, uid, attrs) in XamlUids()) {
			foreach (var (key, text) in resw.Where(r => r.Key.StartsWith(uid + ".", StringComparison.Ordinal))) {
				var property = key[(uid.Length + 1)..];
				var attribute = property.StartsWith("[using:", StringComparison.Ordinal) ? property[(property.IndexOf(']') + 1)..] : property;
				Assert.True(attrs.TryGetValue(attribute, out var literal), $"{file}: {key} has no {attribute} attribute to fall back on");
				Assert.True(literal == text, $"{file}: {key} differs from the XAML text (\"{literal}\" vs \"{text}\")");
			}
		}
	}

	[Fact]
	public void Every_loc_call_has_a_matching_resource_entry() {
		var resw = Resw();
		var calls = 0;
		foreach (var file in Files("*.cs").Where(f => Path.GetFileName(f) != "Loc.cs")) {
			foreach (Match call in LocCall().Matches(File.ReadAllText(file))) {
				calls++;
				var key = call.Groups["key"].Value;
				var english = Unescape(call.Groups["text"].Value);
				Assert.True(resw.TryGetValue(key, out var stored), $"{Path.GetFileName(file)}: Loc key \"{key}\" is not in Resources.resw (run the sync script)");
				Assert.True(stored == english, $"{Path.GetFileName(file)}: Loc key \"{key}\" has different English in code (\"{english}\") and in Resources.resw (\"{stored}\")");
			}
		}
		Assert.True(calls > 100, "expected the UI code to use Loc");
	}

	[Fact]
	public void Format_strings_only_use_placeholders_the_call_supplies() {
		foreach (var file in Files("*.cs").Where(f => Path.GetFileName(f) != "Loc.cs")) {
			var text = File.ReadAllText(file);
			foreach (Match call in Regex.Matches(text, @"Loc\.F\(\s*""[A-Za-z0-9_]+""\s*,\s*""(?<text>(?:[^""\\]|\\.)*)""(?<rest>[^;]*)")) {
				var highest = Regex.Matches(call.Groups["text"].Value, @"\{(\d+)[:}]").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max();
				// Count the arguments after the English text at the call's own nesting depth.
				var args = 0;
				var depth = 0;
				var inString = false;
				foreach (var c in call.Groups["rest"].Value) {
					if (c == '"') { inString = !inString; } else if (inString) { continue; } else if (c is '(' or '[' or '{') { depth++; } else if (c is ')' or ']' or '}') {
						if (depth == 0) { break; }
						depth--;
					} else if (c == ',' && depth == 0) { args++; }
				}
				Assert.True(args >= highest + 1, $"{Path.GetFileName(file)}: \"{call.Groups["text"].Value}\" uses {{{highest}}} but the call passes {args} argument(s)");
			}
		}
	}

	[Fact]
	public void The_resource_file_sits_where_the_winui_build_looks_for_it_and_nothing_removes_it() {
		Assert.True(File.Exists(Path.Combine(AppDir, "Strings", "en-US", "Resources.resw")));
		var csproj = File.ReadAllText(Path.Combine(AppDir, "Aria2NET.csproj"));
		Assert.DoesNotContain("resw", csproj, StringComparison.OrdinalIgnoreCase); // the default item globs pick it up; no custom item should exclude it
	}
}
