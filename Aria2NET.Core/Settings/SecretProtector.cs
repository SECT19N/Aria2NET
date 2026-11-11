using System.Security.Cryptography;
using System.Text;

namespace Aria2NET.Core.Settings;

/// <summary>Keeps a secret (the proxy password) out of settings.json in plain text: Windows DPAPI, current user only.</summary>
public static class SecretProtector {
	private static readonly byte[] Entropy = "Aria2NET.proxy"u8.ToArray();

	public static string Protect(string plain) {
		if (plain.Length == 0) {
			return "";
		}
		var bytes = Encoding.UTF8.GetBytes(plain);
		return OperatingSystem.IsWindows()
			? Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser))
			: "plain:" + Convert.ToBase64String(bytes); // no DPAPI here; only reached outside Windows
	}

	/// <summary>The original text, or an empty string if there is nothing stored or it cannot be read (another user or PC).</summary>
	public static string Unprotect(string? stored) {
		if (string.IsNullOrEmpty(stored)) {
			return "";
		}
		try {
			if (stored.StartsWith("plain:", StringComparison.Ordinal)) {
				return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
			}
			return OperatingSystem.IsWindows()
				? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser))
				: "";
		}
		catch (Exception ex) when (ex is CryptographicException or FormatException) {
			return "";
		}
	}
}
