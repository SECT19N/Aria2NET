using System.Runtime.InteropServices;
using Aria2NET.Core.Background;

namespace Aria2NET.Services;

/// <summary>The progress bar on the window's taskbar button. UI thread only. Calls that Windows refuses (no button yet, window hidden) are ignored.</summary>
internal sealed class TaskbarProgressBar(IntPtr hwnd) {
	private const int NoProgress = 0, Indeterminate = 1, Normal = 2, Paused = 8;

	private ITaskbarList3? _list;
	private bool _unavailable;

	public void Apply(TaskbarProgress progress) {
		if (!TryGetList(out var list)) {
			return;
		}
		switch (progress.State) {
			case TaskbarProgressState.None:
				list.SetProgressState(hwnd, NoProgress);
				break;
			case TaskbarProgressState.Indeterminate:
				list.SetProgressState(hwnd, Indeterminate);
				break;
			default:
				list.SetProgressState(hwnd, progress.State == TaskbarProgressState.Paused ? Paused : Normal);
				list.SetProgressValue(hwnd, progress.Completed, progress.Total);
				break;
		}
	}

	private bool TryGetList(out ITaskbarList3 list) {
		list = _list!;
		if (_list is not null) {
			return true;
		}
		if (_unavailable) {
			return false;
		}
		try {
			var created = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"))!)!;
			created.HrInit();
			_list = list = created;
			return true;
		}
		catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException) {
			_unavailable = true; // no taskbar progress on this system; everything else works without it
			return false;
		}
	}

	// Only the methods up to SetProgressState are declared; the order is the COM vtable order and must not change.
	[ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList3 {
		[PreserveSig] int HrInit();
		[PreserveSig] int AddTab(IntPtr hwnd);
		[PreserveSig] int DeleteTab(IntPtr hwnd);
		[PreserveSig] int ActivateTab(IntPtr hwnd);
		[PreserveSig] int SetActiveAlt(IntPtr hwnd);
		[PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
		[PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
		[PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
	}
}
