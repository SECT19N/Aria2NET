namespace Aria2NET.Core.Settings;

/// <summary>Where the main window was when the app last closed (restored size and position, even if it was maximized).</summary>
public sealed class WindowPlacement {
	public int X { get; set; }
	public int Y { get; set; }
	public int Width { get; set; }
	public int Height { get; set; }
	public bool Maximized { get; set; }
}

public readonly record struct ScreenRect(int X, int Y, int Width, int Height) {
	public int Right => X + Width;
	public int Bottom => Y + Height;

	public ScreenRect Intersect(ScreenRect other) {
		var x = Math.Max(X, other.X);
		var y = Math.Max(Y, other.Y);
		var r = Math.Min(Right, other.Right);
		var b = Math.Min(Bottom, other.Bottom);
		return r > x && b > y ? new ScreenRect(x, y, r - x, b - y) : default;
	}
}

public static class WindowPlacementFitter {
	public const int MinWidth = 640;
	public const int MinHeight = 400;
	private const int MinVisibleWidth = 120;
	private const int MinVisibleHeight = 60;

	/// <summary>
	/// Makes a saved placement safe to use on today's monitors: never off-screen (a monitor may have been unplugged), never bigger
	/// than the screen it is on, never smaller than a usable window. <paramref name="displays"/> are work areas, primary first.
	/// Returns null when there is nothing valid to restore, so the caller keeps the default.
	/// </summary>
	public static WindowPlacement? Fit(WindowPlacement? saved, IReadOnlyList<ScreenRect> displays) {
		if (saved is null || saved.Width <= 0 || saved.Height <= 0 || displays.Count == 0) {
			return null;
		}

		var rect = new ScreenRect(saved.X, saved.Y, Math.Max(saved.Width, MinWidth), Math.Max(saved.Height, MinHeight));
		var target = displays
			.Select(d => (Display: d, Visible: d.Intersect(rect)))
			.Where(p => p.Visible.Width >= MinVisibleWidth && p.Visible.Height >= MinVisibleHeight)
			.OrderByDescending(p => (long)p.Visible.Width * p.Visible.Height)
			.Select(p => (ScreenRect?)p.Display)
			.FirstOrDefault();

		if (target is not { } screen) {
			// Its monitor is gone: centre on the primary one.
			var primary = displays[0];
			var w = Math.Min(rect.Width, primary.Width);
			var h = Math.Min(rect.Height, primary.Height);
			return new WindowPlacement { X = primary.X + (primary.Width - w) / 2, Y = primary.Y + (primary.Height - h) / 2, Width = w, Height = h, Maximized = saved.Maximized };
		}

		var width = Math.Min(rect.Width, screen.Width);
		var height = Math.Min(rect.Height, screen.Height);
		return new WindowPlacement {
			X = Math.Clamp(rect.X, screen.X, screen.Right - width),
			Y = Math.Clamp(rect.Y, screen.Y, screen.Bottom - height),
			Width = width,
			Height = height,
			Maximized = saved.Maximized,
		};
	}
}
