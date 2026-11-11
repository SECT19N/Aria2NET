using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Aria2NET;

/// <summary>
/// Text for the UI from the app's string resources (<c>Strings\en-US\Resources.resw</c>). Every call carries the English text too, so a
/// missing or unloadable resource shows English instead of an empty control; a test keeps the two in step.
/// </summary>
public static class Loc {
	private static readonly Lazy<ResourceLoader?> Loader = new(Create);

	private static ResourceLoader? Create() {
		try {
			return new ResourceLoader();
		} catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or FileNotFoundException) {
			try {
				return new ResourceLoader(Path.Combine(AppContext.BaseDirectory, "Aria2NET.pri"));
			} catch (Exception inner) when (inner is InvalidOperationException or System.Runtime.InteropServices.COMException or FileNotFoundException) {
				return null;
			}
		}
	}

	/// <summary>The resource <paramref name="key"/>, or <paramref name="english"/> when there is none.</summary>
	public static string S(string key, string english) {
		try {
			var text = Loader.Value?.GetString(key);
			return string.IsNullOrEmpty(text) ? english : text;
		} catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) {
			return english;
		}
	}

	/// <summary>Like <see cref="S"/>, then fills in <c>{0}</c>, <c>{1}</c> … with <paramref name="args"/>.</summary>
	public static string F(string key, string english, params object?[] args) =>
		string.Format(CultureInfo.CurrentCulture, S(key, english), args);
}
