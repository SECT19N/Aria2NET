using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Aria2NET.Controls;

/// <summary>A horizontal bar the user drags up or down to resize what lies below it.</summary>
public sealed partial class RowSizer : Grid {
	private bool _dragging;
	private double _startY;

	/// <summary>How far the pointer has moved since the drag began, in pixels; negative is upwards.</summary>
	public event EventHandler<double>? Dragged;

	public event EventHandler? DragStarted;

	public event EventHandler? DragCompleted;

	public RowSizer() {
		Background = new SolidColorBrush(Colors.Transparent); // so the whole bar takes the pointer, not just the grip
		ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
		PointerPressed += OnPressed;
		PointerMoved += OnMoved;
		PointerReleased += OnReleased;
		PointerCaptureLost += OnReleased;
	}

	private void OnPressed(object sender, PointerRoutedEventArgs e) {
		if (CapturePointer(e.Pointer)) {
			_dragging = true;
			// Measured against the window: this bar moves while it is dragged.
			_startY = e.GetCurrentPoint(null).Position.Y;
			DragStarted?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
		}
	}

	private void OnMoved(object sender, PointerRoutedEventArgs e) {
		if (_dragging) {
			Dragged?.Invoke(this, e.GetCurrentPoint(null).Position.Y - _startY);
			e.Handled = true;
		}
	}

	private void OnReleased(object sender, PointerRoutedEventArgs e) {
		if (_dragging) {
			_dragging = false;
			ReleasePointerCapture(e.Pointer);
			DragCompleted?.Invoke(this, EventArgs.Empty);
		}
	}
}
