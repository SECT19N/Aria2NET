using Aria2NET.Core.Torrents;

namespace Aria2NET.Tests;

public class TorrentFileTreeTests {
	private static TorrentFileTree Sample() => TorrentFileTree.Build([
		new(1, "readme.txt", 10),
		new(2, "video/ep1.mkv", 1000),
		new(3, "video/ep2.mkv", 2000),
		new(4, "video/extras/making.mkv", 300),
		new(5, "audio/song.flac", 50),
	]);

	[Fact]
	public void Files_are_grouped_by_folder_with_folders_before_files() {
		var roots = Sample().Roots;

		Assert.Equal(["audio", "video", "readme.txt"], roots.Select(r => r.Name));
		Assert.Equal(["extras", "ep1.mkv", "ep2.mkv"], roots[1].Children.Select(c => c.Name));
	}

	[Fact]
	public void A_folder_that_only_holds_another_folder_is_shown_as_one_row() {
		var tree = TorrentFileTree.Build([new(1, "a/b/c/one.bin", 5), new(2, "a/b/c/two.bin", 7)]);

		var only = Assert.Single(tree.Roots);
		Assert.Equal("a/b/c", only.Name);
		Assert.Equal(2, only.Children.Count);
	}

	[Fact]
	public void Folders_total_their_size_and_file_count() {
		var video = Sample().Roots.Single(r => r.Name == "video");

		Assert.Equal(3300, video.Length);
		Assert.Equal(3, video.FileCount);
		Assert.Equal(3360, Sample().TotalBytes);
	}

	[Fact]
	public void Everything_starts_chosen() {
		var tree = Sample();

		Assert.Equal([1, 2, 3, 4, 5], tree.SelectedIndexes());
		Assert.All(tree.Roots, r => Assert.True(r.IsChecked));
	}

	[Fact]
	public void Unchecking_a_folder_unchecks_everything_inside_it() {
		var tree = Sample();
		tree.Roots.Single(r => r.Name == "video").IsChecked = false;

		Assert.Equal([1, 5], tree.SelectedIndexes());
		Assert.Equal(60, tree.SelectedBytes);
	}

	[Fact]
	public void One_unchecked_file_leaves_its_folders_partly_chosen() {
		var tree = Sample();
		var video = tree.Roots.Single(r => r.Name == "video");
		video.Children.Single(c => c.Name == "extras").Children[0].IsChecked = false;

		Assert.False(video.Children.Single(c => c.Name == "extras").IsChecked);
		Assert.Null(video.IsChecked);
		Assert.Equal([1, 2, 3, 5], tree.SelectedIndexes());
	}

	[Fact]
	public void Unchecking_every_file_in_a_folder_unchecks_the_folder_and_checking_one_back_makes_it_partial() {
		var tree = Sample();
		var audio = tree.Roots.Single(r => r.Name == "audio");
		audio.Children[0].IsChecked = false;
		Assert.False(audio.IsChecked);

		audio.Children[0].IsChecked = true;
		Assert.True(audio.IsChecked);

		var video = tree.Roots.Single(r => r.Name == "video");
		foreach (var file in video.Children.Where(c => !c.IsFolder)) {
			file.IsChecked = false;
		}
		Assert.Null(video.IsChecked);
	}

	[Fact]
	public void A_folder_cannot_be_set_to_partial_by_hand() {
		var tree = Sample();
		var video = tree.Roots.Single(r => r.Name == "video");
		video.IsChecked = null;

		Assert.True(video.IsChecked);
	}

	[Fact]
	public void SetAll_chooses_or_clears_everything() {
		var tree = Sample();
		tree.SetAll(false);
		Assert.Empty(tree.SelectedIndexes());

		tree.SetAll(true);
		Assert.Equal(5, tree.SelectedCount);
	}

	[Fact]
	public void Changing_a_check_state_raises_PropertyChanged_on_the_folder() {
		var tree = Sample();
		var video = tree.Roots.Single(r => r.Name == "video");
		var raised = 0;
		video.PropertyChanged += (_, _) => raised++;

		video.Children.First(c => !c.IsFolder).IsChecked = false;

		Assert.Equal(1, raised);
	}

	[Fact]
	public void A_single_file_torrent_is_one_row() {
		var tree = TorrentFileTree.Build([new(1, "movie.iso", 4_000)]);

		var leaf = Assert.Single(tree.Roots);
		Assert.False(leaf.IsFolder);
		Assert.Equal([1], tree.SelectedIndexes());
	}

	[Fact]
	public void Five_hundred_files_in_nested_folders_build_and_toggle() {
		var files = Enumerable.Range(1, 500).Select(i => new TorrentFileEntry(i, $"disc{i % 5}/part{i % 20}/file{i}.bin", 100)).ToList();
		var tree = TorrentFileTree.Build(files);

		Assert.Equal(500, tree.SelectedCount);
		tree.Roots[0].IsChecked = false;
		Assert.Equal(400, tree.SelectedCount);
		Assert.Equal(files.Where(f => f.Index % 5 != 0).Select(f => f.Index), tree.SelectedIndexes());
	}
}
