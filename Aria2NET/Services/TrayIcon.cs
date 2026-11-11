using System.Runtime.InteropServices;
using Aria2NET.Core.Background;
using Aria2NET.Core.Tray;
using Microsoft.Extensions.Logging;

namespace Aria2NET.Services;

/// <summary>
/// The notification-area icon with its right-click menu. Its messages arrive by subclassing the main window, so no extra window class is
/// needed. Use it on the UI thread only.
/// </summary>
internal sealed class TrayIcon : IDisposable {
	private const uint CallbackMessage = 0x8001; // WM_APP + 1
	private const uint WmLButtonUp = 0x0202;
	private const uint WmRButtonUp = 0x0205;
	private const uint IconId = 1;

	private const int MenuOpen = 1, MenuPauseAll = 2, MenuResumeAll = 3, MenuOpenFolder = 4, MenuExit = 5, MenuFinishBase = 100;

	private readonly IntPtr _hwnd;
	private readonly ILogger _logger;
	private readonly SubclassProc _proc; // referenced so the delegate outlives the native subclass
	private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
	private readonly Dictionary<TrayState, IntPtr> _icons = [];
	private TrayState _state = TrayState.Idle;
	private string _tooltip = "Aria2NET";
	private bool _added;

	public TrayIcon(IntPtr hwnd, string iconPath, ILogger logger) {
		_hwnd = hwnd;
		_logger = logger;
		_proc = OnMessage;
		var size = GetSystemMetrics(49 /* SM_CXSMICON */);
		foreach (var state in Enum.GetValues<TrayState>()) {
			var icon = LoadBadged(iconPath, size, state);
			if (icon != IntPtr.Zero) {
				_icons[state] = icon;
			}
		}
		SetWindowSubclass(hwnd, _proc, UIntPtr.Zero, UIntPtr.Zero);
	}

	/// <summary>True once Windows accepted the icon. Closing to the tray is only safe while this holds.</summary>
	public bool IsVisible => _added;

	public event Action? OpenRequested;
	public event Action? PauseAllRequested;
	public event Action? ResumeAllRequested;
	public event Action? OpenFolderRequested;
	public event Action? ExitRequested;
	/// <summary>Windows is signing out or shutting down and will end the process soon.</summary>
	public event Action? SessionEnding;
	public event Action<int>? HotkeyPressed;
	public event Action<FinishAction>? FinishActionChosen;

	/// <summary>Which "When finished" entry is ticked in the menu.</summary>
	public Func<FinishAction>? CurrentFinishAction { get; set; }

	public void Show() => Add();

	/// <summary>Registers a global hotkey that arrives as <see cref="HotkeyPressed"/>. False when another program already owns it.</summary>
	public bool RegisterHotkey(int id, uint modifiers, uint key) => RegisterHotKey(_hwnd, id, modifiers | 0x4000 /* MOD_NOREPEAT */, key);

	public void UnregisterHotkey(int id) => UnregisterHotKey(_hwnd, id);

	public void Update(TrayState state, string tooltip) {
		if (state == _state && tooltip == _tooltip) {
			return;
		}
		_state = state;
		_tooltip = tooltip;
		if (_added) {
			Notify(1 /* NIM_MODIFY */);
		}
	}

	private void Add() {
		_added = Notify(0 /* NIM_ADD */);
		if (!_added) {
			_logger.LogWarning("Could not add the notification-area icon");
		}
	}

	private bool Notify(uint message) {
		var data = new NotifyIconData {
			cbSize = Marshal.SizeOf<NotifyIconData>(),
			hWnd = _hwnd,
			uID = IconId,
			uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
			uCallbackMessage = CallbackMessage,
			hIcon = _icons.GetValueOrDefault(_state),
			szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
			szInfo = "",
			szInfoTitle = "",
		};
		return Shell_NotifyIcon(message, ref data);
	}

	private IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) {
		try {
			if (message == CallbackMessage) {
				switch ((uint)(lParam.ToInt64() & 0xFFFF)) {
					case WmLButtonUp:
						OpenRequested?.Invoke();
						break;
					case WmRButtonUp:
						ShowMenu();
						break;
				}
				return IntPtr.Zero;
			}
			if (message == 0x0312 /* WM_HOTKEY */) {
				HotkeyPressed?.Invoke((int)wParam);
				return IntPtr.Zero;
			}
			if (message == 0x0016 /* WM_ENDSESSION */ && wParam != IntPtr.Zero) {
				SessionEnding?.Invoke();
			}
			else if (message == _taskbarCreated && _taskbarCreated != 0) {
				Add(); // Explorer restarted and dropped every icon
			}
		}
		catch (Exception ex) {
			_logger.LogError(ex, "Tray message handling failed");
		}
		return DefSubclassProc(hwnd, message, wParam, lParam);
	}

	private void ShowMenu() {
		var menu = CreatePopupMenu();
		try {
			AppendMenu(menu, 0, MenuOpen, Loc.S("Tray_Open", "Open Aria2NET"));
			AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
			AppendMenu(menu, 0, MenuPauseAll, Loc.S("Tray_PauseAll", "Pause all"));
			AppendMenu(menu, 0, MenuResumeAll, Loc.S("Tray_ResumeAll", "Resume all"));
			AppendMenu(menu, 0, MenuOpenFolder, Loc.S("Tray_OpenFolder", "Open download folder"));
			AppendMenu(menu, 0x800, 0, null);
			var finish = CreatePopupMenu(); // destroyed together with its parent
			var current = CurrentFinishAction?.Invoke() ?? FinishAction.None;
			foreach (var action in Enum.GetValues<FinishAction>()) {
				AppendMenu(finish, action == current ? 0x8u /* MF_CHECKED */ : 0u, (nuint)(MenuFinishBase + (int)action), WhenFinishedMachine.Label(action));
			}
			AppendMenu(menu, 0x10 /* MF_POPUP */, (nuint)finish, Loc.S("Tray_WhenFinished", "When downloads finish"));
			AppendMenu(menu, 0x800, 0, null);
			AppendMenu(menu, 0, MenuExit, Loc.S("Tray_Exit", "Exit"));
			SetMenuDefaultItem(menu, MenuOpen, 0);

			GetCursorPos(out var point);
			SetForegroundWindow(_hwnd); // without this the menu does not close when the user clicks elsewhere
			var command = TrackPopupMenu(menu, 0x100 | 0x2 /* TPM_RETURNCMD | TPM_RIGHTBUTTON */, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
			PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
			switch (command) {
				case MenuOpen: OpenRequested?.Invoke(); break;
				case MenuPauseAll: PauseAllRequested?.Invoke(); break;
				case MenuResumeAll: ResumeAllRequested?.Invoke(); break;
				case MenuOpenFolder: OpenFolderRequested?.Invoke(); break;
				case MenuExit: ExitRequested?.Invoke(); break;
				case >= MenuFinishBase: FinishActionChosen?.Invoke((FinishAction)(command - MenuFinishBase)); break;
			}
		}
		finally {
			DestroyMenu(menu);
		}
	}

	public void Dispose() {
		if (_added) {
			Notify(2 /* NIM_DELETE */);
			_added = false;
		}
		RemoveWindowSubclass(_hwnd, _proc, UIntPtr.Zero);
		foreach (var icon in _icons.Values) {
			DestroyIcon(icon);
		}
		_icons.Clear();
	}

	// ---------------------------------------------------------------- icon drawing

	/// <summary>The app icon at <paramref name="size"/> pixels with the state's dot painted on; the plain icon for Idle. Zero when the file cannot be loaded.</summary>
	private static IntPtr LoadBadged(string path, int size, TrayState state) {
		var icon = LoadImage(IntPtr.Zero, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
		if (icon == IntPtr.Zero || TrayBadge.ColorOf(state) is not { } color) {
			return icon;
		}
		if (!GetIconInfo(icon, out var info)) {
			return icon;
		}
		try {
			if (info.hbmColor == IntPtr.Zero || GetObject(info.hbmColor, Marshal.SizeOf<Bitmap>(), out var bitmap) == 0 || bitmap.bmWidth != size || bitmap.bmHeight != size) {
				return icon; // not a 32-bit icon of the expected size
			}
			var pixels = new byte[size * size * 4];
			var header = new BitmapInfoHeader { biSize = Marshal.SizeOf<BitmapInfoHeader>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
			var screen = GetDC(IntPtr.Zero);
			var read = GetDIBits(screen, info.hbmColor, 0, (uint)size, pixels, ref header, 0);
			ReleaseDC(IntPtr.Zero, screen);
			if (read == 0) {
				return icon;
			}
			TrayBadge.Paint(pixels, size, color);

			var painted = CreateBitmap(size, size, 1, 32, pixels);
			var composed = CreateIconIndirect(new IconInfo { fIcon = true, hbmMask = info.hbmMask, hbmColor = painted });
			DeleteObject(painted);
			if (composed == IntPtr.Zero) {
				return icon;
			}
			DestroyIcon(icon);
			return composed;
		}
		finally {
			if (info.hbmColor != IntPtr.Zero) {
				DeleteObject(info.hbmColor);
			}
			if (info.hbmMask != IntPtr.Zero) {
				DeleteObject(info.hbmMask);
			}
		}
	}

	// ---------------------------------------------------------------- native

	private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData {
		public int cbSize;
		public IntPtr hWnd;
		public uint uID;
		public uint uFlags;
		public uint uCallbackMessage;
		public IntPtr hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
		public uint dwState;
		public uint dwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
		public uint uTimeoutOrVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
		public uint dwInfoFlags;
		public Guid guidItem;
		public IntPtr hBalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IconInfo {
		[MarshalAs(UnmanagedType.Bool)] public bool fIcon;
		public uint xHotspot;
		public uint yHotspot;
		public IntPtr hbmMask;
		public IntPtr hbmColor;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Bitmap {
		public int bmType;
		public int bmWidth;
		public int bmHeight;
		public int bmWidthBytes;
		public ushort bmPlanes;
		public ushort bmBitsPixel;
		public IntPtr bmBits;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapInfoHeader {
		public int biSize;
		public int biWidth;
		public int biHeight;
		public ushort biPlanes;
		public ushort biBitCount;
		public uint biCompression;
		public uint biSizeImage;
		public int biXPelsPerMeter;
		public int biYPelsPerMeter;
		public uint biClrUsed;
		public uint biClrImportant;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point {
		public int X;
		public int Y;
	}

	[DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

	[DllImport("comctl32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

	[DllImport("comctl32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

	[DllImport("comctl32.dll")]
	private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint RegisterWindowMessage(string name);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

	[DllImport("user32.dll")]
	private static extern IntPtr CreateIconIndirect(in IconInfo info);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(IntPtr icon);

	[DllImport("user32.dll")]
	private static extern IntPtr CreatePopupMenu();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

	[DllImport("user32.dll")]
	private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyMenu(IntPtr menu);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hwnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDC(IntPtr hwnd);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

	[DllImport("gdi32.dll")]
	private static extern int GetObject(IntPtr handle, int count, out Bitmap bitmap);

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(IntPtr handle);
}
