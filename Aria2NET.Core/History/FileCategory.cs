namespace Aria2NET.Core.History;

/// <summary>Coarse file type used for the History filter, taken from the file name.</summary>
public static class FileCategory {
	public const string Video = "Video";
	public const string Audio = "Audio";
	public const string Image = "Image";
	public const string Archive = "Archive";
	public const string Document = "Document";
	public const string Program = "Program";
	public const string Torrent = "Torrent";
	public const string Other = "Other";

	public static IReadOnlyList<string> All { get; } = [Video, Audio, Image, Archive, Document, Program, Torrent, Other];

	private static readonly Dictionary<string, string> ByExtension = Build();

	public static string FromName(string? name) {
		var ext = Path.GetExtension(name ?? "");
		return ext.Length > 1 && ByExtension.TryGetValue(ext[1..], out var category) ? category : Other;
	}

	private static Dictionary<string, string> Build() {
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		void Add(string category, string extensions) {
			foreach (var ext in extensions.Split(' ')) {
				map[ext] = category;
			}
		}
		Add(Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts");
		Add(Audio, "mp3 flac wav aac ogg opus m4a wma");
		Add(Image, "jpg jpeg png gif bmp webp svg tif tiff ico heic avif");
		Add(Archive, "zip rar 7z tar gz bz2 xz zst iso cab");
		Add(Document, "pdf doc docx xls xlsx ppt pptx txt rtf odt epub csv md");
		Add(Program, "exe msi msix msixbundle appx apk dmg pkg bat ps1 jar");
		Add(Torrent, "torrent metalink meta4");
		return map;
	}
}
