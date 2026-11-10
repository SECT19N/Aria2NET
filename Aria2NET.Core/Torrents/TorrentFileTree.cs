using System.ComponentModel;

namespace Aria2NET.Core.Torrents;

/// <summary>A folder or a file in the picker. Folders show the total of what is inside and a check state derived from their children.</summary>
public sealed class FileTreeNode : INotifyPropertyChanged {
	private bool? _isChecked = true;

	internal FileTreeNode(string name, bool isFolder, int index, long length, int fileCount) {
		Name = name;
		IsFolder = isFolder;
		Index = index;
		Length = length;
		FileCount = fileCount;
	}

	/// <summary>The file or folder name. A folder that only holds one other folder is shown as one row, so this can be <c>a/b/c</c>.</summary>
	public string Name { get; }

	public bool IsFolder { get; }

	/// <summary>1-based file index for aria2's <c>select-file</c>; 0 for a folder.</summary>
	public int Index { get; }

	/// <summary>Size of the file, or of everything inside the folder.</summary>
	public long Length { get; internal set; }

	/// <summary>1 for a file, the number of files inside for a folder.</summary>
	public int FileCount { get; internal set; }

	public FileTreeNode? Parent { get; internal set; }

	public IReadOnlyList<FileTreeNode> Children { get; internal set; } = [];

	/// <summary>True, false, or null for a folder that is partly chosen. Setting a folder sets everything inside it; null cannot be set, it is only ever worked out.</summary>
	public bool? IsChecked {
		get => _isChecked;
		set {
			if (value is not { } on) {
				return;
			}
			SetDown(on);
			Parent?.Refresh();
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void SetDown(bool value) {
		foreach (var child in Children) {
			child.SetDown(value);
		}
		Store(value);
	}

	private void Refresh() {
		var all = true;
		var none = true;
		foreach (var child in Children) {
			all &= child._isChecked == true;
			none &= child._isChecked == false;
		}
		Store(all ? true : none ? false : null);
		Parent?.Refresh();
	}

	private void Store(bool? value) {
		if (_isChecked != value) {
			_isChecked = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
		}
	}
}

/// <summary>The files of a torrent grouped by folder, with the choice of which to download.</summary>
public sealed class TorrentFileTree {
	private readonly List<FileTreeNode> _leaves = [];

	private TorrentFileTree(IReadOnlyList<FileTreeNode> roots) {
		Roots = roots;
	}

	public IReadOnlyList<FileTreeNode> Roots { get; }

	public int TotalCount => _leaves.Count;

	public long TotalBytes => _leaves.Sum(l => l.Length);

	/// <summary>Everything starts chosen. Paths use <c>/</c> as in the .torrent; folders come before files, each ordered by name.</summary>
	public static TorrentFileTree Build(IEnumerable<TorrentFileEntry> files) {
		var top = new Folder("");
		foreach (var file in files) {
			var parts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0) {
				continue;
			}
			var folder = top;
			foreach (var part in parts[..^1]) {
				if (!folder.Folders.TryGetValue(part, out var next)) {
					folder.Folders[part] = next = new Folder(part);
				}
				folder = next;
			}
			folder.Files.Add((parts[^1], file));
		}

		var leaves = new List<FileTreeNode>();
		var roots = Convert(top, null, leaves);
		var tree = new TorrentFileTree(roots);
		tree._leaves.AddRange(leaves.OrderBy(l => l.Index));
		return tree;
	}

	public IReadOnlyList<int> SelectedIndexes() => _leaves.Where(l => l.IsChecked == true).Select(l => l.Index).ToList();

	public int SelectedCount => _leaves.Count(l => l.IsChecked == true);

	public long SelectedBytes => _leaves.Where(l => l.IsChecked == true).Sum(l => l.Length);

	public void SetAll(bool value) {
		foreach (var root in Roots) {
			root.IsChecked = value;
		}
	}

	private static List<FileTreeNode> Convert(Folder folder, FileTreeNode? parent, List<FileTreeNode> leaves) {
		var nodes = new List<FileTreeNode>();
		foreach (var child in folder.Folders.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) {
			var shown = child;
			var name = child.Name;
			while (shown.Files.Count == 0 && shown.Folders.Count == 1) {
				shown = shown.Folders.Values.Single();
				name += "/" + shown.Name;
			}
			var node = new FileTreeNode(name, isFolder: true, index: 0, length: 0, fileCount: 0) { Parent = parent };
			node.Children = Convert(shown, node, leaves);
			node.Length = node.Children.Sum(c => c.Length);
			node.FileCount = node.Children.Sum(c => c.FileCount);
			nodes.Add(node);
		}
		foreach (var (name, entry) in folder.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) {
			var leaf = new FileTreeNode(name, isFolder: false, entry.Index, entry.Length, fileCount: 1) { Parent = parent };
			leaves.Add(leaf);
			nodes.Add(leaf);
		}
		return nodes;
	}

	private sealed class Folder(string name) {
		public string Name { get; } = name;
		public Dictionary<string, Folder> Folders { get; } = new(StringComparer.Ordinal);
		public List<(string Name, TorrentFileEntry Entry)> Files { get; } = [];
	}
}
