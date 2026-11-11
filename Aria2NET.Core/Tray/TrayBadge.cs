namespace Aria2NET.Core.Tray;

/// <summary>Draws the state dot in the corner of a tray icon. Works on raw 32-bit BGRA pixels so it needs no imaging library.</summary>
public static class TrayBadge {
	/// <summary>Dot colour (R, G, B) for a state; null for Idle, which shows the plain app icon.</summary>
	public static (byte R, byte G, byte B)? ColorOf(TrayState state) => state switch {
		TrayState.Downloading => (46, 160, 67),
		TrayState.Paused => (230, 160, 20),
		TrayState.Error => (215, 58, 73),
		_ => null,
	};

	/// <summary>Paints an opaque dot with a white rim into the bottom-right corner of a <paramref name="size"/> x <paramref name="size"/> BGRA image.</summary>
	public static void Paint(byte[] bgra, int size, (byte R, byte G, byte B) color) {
		var radius = Math.Max(3.0, size * 0.25);
		var centre = size - radius - 1.5;
		for (var y = 0; y < size; y++) {
			for (var x = 0; x < size; x++) {
				var distance = Math.Sqrt((x + 0.5 - centre) * (x + 0.5 - centre) + (y + 0.5 - centre) * (y + 0.5 - centre));
				if (distance > radius + 1) {
					continue;
				}
				var i = (y * size + x) * 4;
				var (r, g, b) = distance <= radius ? color : ((byte) 255, (byte) 255, (byte) 255);
				bgra[i] = b;
				bgra[i + 1] = g;
				bgra[i + 2] = r;
				bgra[i + 3] = 255;
			}
		}
	}
}
