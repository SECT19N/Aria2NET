using System.Runtime.InteropServices;

namespace Aria2NET.Services;

/// <summary>Keeps a window from being dragged smaller than a given size, so the layout never has to cope with a few hundred pixels.</summary>
internal sealed class MinWindowSize {
	private const uint WmGetMinMaxInfo = 0x0024;

	private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

	[StructLayout(LayoutKind.Sequential)]
	private struct Point {
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MinMaxInfo {
		public Point Reserved;
		public Point MaxSize;
		public Point MaxPosition;
		public Point MinTrackSize;
		public Point MaxTrackSize;
	}

	private readonly SubclassProc _proc; // held so the callback is not collected while Windows can still call it
	private readonly int _width;
	private readonly int _height;

	/// <summary>Sizes are in device-independent pixels (what the layout works in); they are scaled by the window's DPI.</summary>
	public MinWindowSize(IntPtr hwnd, int width, int height) {
		_width = width;
		_height = height;
		_proc = OnMessage;
		SetWindowSubclass(hwnd, _proc, new UIntPtr(0x4D494E), UIntPtr.Zero); // "MIN": TrayIcon uses id 0
	}

	private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) {
		if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero) {
			MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
			double scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;
			info.MinTrackSize.X = (int) Math.Round(_width * scale);
			info.MinTrackSize.Y = (int) Math.Round(_height * scale);
			Marshal.StructureToPtr(info, lParam, false);
		}
		return DefSubclassProc(hwnd, message, wParam, lParam);
	}

	[DllImport("comctl32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

	[DllImport("comctl32.dll")]
	private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(IntPtr hwnd);
}
