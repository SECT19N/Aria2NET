using Aria2NET.Core.Shell;

namespace Aria2NET.Tests;

public class ShellLauncherTests {
	[Fact]
	public void An_existing_file_is_selected_in_explorer() {
		using TempDir temp = new TempDir();
		string file = System.IO.Path.Combine(temp.Path, "a.zip");
		File.WriteAllText(file, "x");

		Assert.Equal($"/select,\"{file}\"", ShellLauncher.FolderArguments(temp.Path, file));
	}

	[Fact]
	public void A_missing_file_opens_the_folder_instead() {
		using TempDir temp = new TempDir();
		string file = System.IO.Path.Combine(temp.Path, "gone.zip");

		Assert.Equal($"\"{temp.Path}\"", ShellLauncher.FolderArguments(temp.Path, file));
		Assert.Equal($"\"{temp.Path}\"", ShellLauncher.FolderArguments(temp.Path, null));
	}
}

public class ShellLauncherPathTests {
	[Fact]
	public void Forward_slashes_from_aria2_are_normalised_so_Explorer_selects_the_file() {
		using TempDir temp = new TempDir();
		string file = System.IO.Path.Combine(temp.Path, "a.zip");
		File.WriteAllText(file, "x");

		Assert.Equal($"/select,\"{file}\"", ShellLauncher.FolderArguments(temp.Path, temp.Path + "/a.zip"));
	}

	[Fact]
	public void A_missing_folder_opens_its_nearest_existing_parent() {
		using TempDir temp = new TempDir();

		Assert.Equal($"\"{temp.Path}\"", ShellLauncher.FolderArguments(System.IO.Path.Combine(temp.Path, "gone", "deeper"), null));
	}
}

public class ShellLauncherHighlightTests {
	[Fact]
	public void A_file_inside_a_torrent_folder_highlights_the_folder() {
		using TempDir temp = new TempDir();
		string folder = System.IO.Path.Combine(temp.Path, "Show");
		Directory.CreateDirectory(System.IO.Path.Combine(folder, "sub"));
		string file = System.IO.Path.Combine(folder, "sub", "a.mkv");
		File.WriteAllText(file, "x");

		Assert.Equal($"/select,\"{folder}\"", ShellLauncher.FolderArguments(temp.Path, file));
	}

	[Fact]
	public void A_torrent_folder_is_highlighted_even_before_its_first_file_exists() {
		using TempDir temp = new TempDir();
		string folder = System.IO.Path.Combine(temp.Path, "Show");
		Directory.CreateDirectory(folder);

		Assert.Equal($"/select,\"{folder}\"", ShellLauncher.FolderArguments(temp.Path, System.IO.Path.Combine(folder, "a.mkv")));
	}
}
