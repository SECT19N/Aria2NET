using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Aria2NET.Core.Shell;

/// <summary>
/// Tells Chrome and Edge that the Aria2NET extension may start this app as its native messaging host: a small manifest file naming the exe
/// and the extensions allowed to use it, and one registry value per browser pointing at that file. Current user only (HKCU), no admin rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeMessagingRegistration(string exePath, string manifestDir, RegistryKey? software = null) {
	/// <summary>Under HKCU\Software. Other Chromium browsers have their own key and can be added here.</summary>
	private static readonly string[] BrowserKeys = [@"Google\Chrome\NativeMessagingHosts", @"Microsoft\Edge\NativeMessagingHosts"];

	/// <summary>HKCU\Software, or a throwaway key in tests.</summary>
	private readonly RegistryKey _software = software ?? Registry.CurrentUser.OpenSubKey("Software", writable: true)!;

	public string ManifestPath { get; } = Path.Combine(manifestDir, BrowserBridge.HostName + ".json");

	/// <summary>Writes the manifest and points the browsers at it. Safe to repeat; it also repairs the path after the app has moved.</summary>
	public void Register() {
		Directory.CreateDirectory(manifestDir);
		File.WriteAllText(ManifestPath, ManifestJson(), new UTF8Encoding(false));
		foreach (string browser in BrowserKeys) {
			using RegistryKey key = _software.CreateSubKey($@"{browser}\{BrowserBridge.HostName}", writable: true);
			key.SetValue(null, ManifestPath);
		}
	}

	/// <summary>Removes everything <see cref="Register"/> wrote. Safe to call when nothing is registered.</summary>
	public void Unregister() {
		foreach (string browser in BrowserKeys) {
			using RegistryKey? key = _software.OpenSubKey(browser, writable: true);
			key?.DeleteSubKeyTree(BrowserBridge.HostName, throwOnMissingSubKey: false);
		}
		if (File.Exists(ManifestPath)) {
			File.Delete(ManifestPath);
		}
	}

	/// <summary>True when every browser is pointed at a manifest that names this exe.</summary>
	public bool IsRegistered() {
		if (!File.Exists(ManifestPath) || !string.Equals(File.ReadAllText(ManifestPath), ManifestJson(), StringComparison.Ordinal)) {
			return false;
		}
		foreach (string browser in BrowserKeys) {
			using RegistryKey? key = _software.OpenSubKey($@"{browser}\{BrowserBridge.HostName}");
			if (!string.Equals(key?.GetValue(null) as string, ManifestPath, StringComparison.OrdinalIgnoreCase)) {
				return false;
			}
		}
		return true;
	}

	internal string ManifestJson() {
		using MemoryStream stream = new MemoryStream();
		using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) {
			writer.WriteStartObject();
			writer.WriteString("name", BrowserBridge.HostName);
			writer.WriteString("description", "Hands browser downloads to Aria2NET");
			writer.WriteString("path", exePath);
			writer.WriteString("type", "stdio");
			writer.WriteStartArray("allowed_origins");
			foreach (string origin in BrowserBridge.AllowedOrigins) {
				writer.WriteStringValue(origin);
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
