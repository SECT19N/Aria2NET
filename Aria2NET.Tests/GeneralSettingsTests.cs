using System.Text.Json;
using Aria2NET.Core.Downloads;
using Aria2NET.Core.Settings;

namespace Aria2NET.Tests;

public class ConflictSettingsTests {
	[Theory]
	[InlineData(ConflictPolicy.AutoRename, "false", "true")]
	[InlineData(ConflictPolicy.Ask, "false", "true")]
	[InlineData(ConflictPolicy.Overwrite, "true", "false")]
	public void Policy_maps_to_aria2_overwrite_and_renaming(ConflictPolicy policy, string allowOverwrite, string autoRename) {
		var o = EngineOptions.LiveOptions(new AppSettings { Conflict = policy });
		Assert.Equal(allowOverwrite, o["allow-overwrite"]);
		Assert.Equal(autoRename, o["auto-file-renaming"]);
	}

	[Fact]
	public void Old_settings_files_get_the_new_general_defaults() {
		var s = JsonSerializer.Deserialize("""{ "SchemaVersion": 1 }""", AppSettingsJsonContext.Default.AppSettings)!;
		Assert.Equal(AppTheme.System, s.Theme);
		Assert.Equal(ConflictPolicy.AutoRename, s.Conflict);
	}

	[Fact]
	public void Theme_and_conflict_survive_a_save_and_load() {
		using var temp = new TempDir();
		var store = new SettingsStore<AppSettings>(Path.Combine(temp.Path, "s.json"), AppSettingsJsonContext.Default.AppSettings);
		store.Save(new AppSettings { Theme = AppTheme.Dark, Conflict = ConflictPolicy.Ask });
		var loaded = store.Load();
		Assert.Equal(AppTheme.Dark, loaded.Theme);
		Assert.Equal(ConflictPolicy.Ask, loaded.Conflict);
	}
}

public class FileConflictsTests {
	[Fact]
	public void Finds_a_file_by_the_name_in_the_link_or_the_typed_name() {
		using var temp = new TempDir();
		File.WriteAllText(Path.Combine(temp.Path, "report final.pdf"), "x");

		Assert.Equal(Path.Combine(temp.Path, "report final.pdf"), FileConflicts.FindExisting(temp.Path, "https://example.com/a/report%20final.pdf?token=1", null));
		Assert.Equal(Path.Combine(temp.Path, "report final.pdf"), FileConflicts.FindExisting(temp.Path, "https://example.com/download?id=7", "report final.pdf"));
		Assert.Null(FileConflicts.FindExisting(temp.Path, "https://example.com/other.pdf", null));
	}

	[Fact]
	public void An_unfinished_aria2_download_is_not_a_conflict() {
		using var temp = new TempDir();
		File.WriteAllText(Path.Combine(temp.Path, "big.iso"), "x");
		File.WriteAllText(Path.Combine(temp.Path, "big.iso.aria2"), "x");
		Assert.Null(FileConflicts.FindExisting(temp.Path, "https://example.com/big.iso", null));
	}

	[Theory]
	[InlineData("https://example.com/")]
	[InlineData("magnet:?xt=urn:btih:abc")]
	[InlineData("not a link")]
	public void Links_without_a_usable_name_are_never_a_conflict(string url) {
		using var temp = new TempDir();
		Assert.Null(FileConflicts.FindExisting(temp.Path, url, null));
	}

	[Fact]
	public void Names_that_are_not_file_names_are_ignored() {
		using var temp = new TempDir();
		Assert.Null(FileConflicts.FindExisting(temp.Path, "https://example.com/x", "a|b?.txt"));
	}

	[Fact]
	public void Each_choice_turns_off_resume_so_the_old_file_is_not_treated_as_a_partial_download() {
		Assert.Equal("false", FileConflicts.OptionsFor(ConflictChoice.Rename)["continue"]);
		Assert.Equal("true", FileConflicts.OptionsFor(ConflictChoice.Overwrite)["allow-overwrite"]);
		Assert.Equal("false", FileConflicts.OptionsFor(ConflictChoice.Overwrite)["auto-file-renaming"]);
		Assert.Empty(FileConflicts.OptionsFor(ConflictChoice.Skip));
	}

	[Fact]
	public void Policy_decides_without_asking_except_for_ask() {
		Assert.Equal(ConflictChoice.Rename, FileConflicts.FromPolicy(ConflictPolicy.AutoRename));
		Assert.Equal(ConflictChoice.Overwrite, FileConflicts.FromPolicy(ConflictPolicy.Overwrite));
		Assert.Null(FileConflicts.FromPolicy(ConflictPolicy.Ask));
	}
}
