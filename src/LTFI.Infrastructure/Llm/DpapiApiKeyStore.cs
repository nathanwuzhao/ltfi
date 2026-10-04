using System.Security.Cryptography;
using System.Text;
using LTFI.Core.Abstractions;

namespace LTFI.Infrastructure.Llm;

/// <summary>
/// Stores the OpenAI key in a DPAPI-protected file (<c>%AppData%/LTFI/openai.key</c>), encrypted to
/// the current Windows user. The <c>OPENAI_API_KEY</c> environment variable, when set, takes
/// precedence. The key is never logged and never written to SQLite. DPAPI keys don't roam, so a new
/// machine or Windows reinstall means pasting the key again — acceptable.
/// </summary>
public sealed class DpapiApiKeyStore(string filePath, string envVarName = DpapiApiKeyStore.DefaultEnvVar) : IApiKeyStore
{
    public const string DefaultEnvVar = "OPENAI_API_KEY";
    public const string FileName = "openai.key";

    // Extra entropy so another app running as the same user can't trivially unprotect the blob.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LTFI.v1");

    public string? KeySource
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVarName))) return $"env:{envVarName}";
            return ReadFile() is null ? null : "file";
        }
    }

    public string? GetKey()
    {
        var env = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return ReadFile();
    }

    public void SetKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key must not be empty.", nameof(key));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException($"Encrypted key storage needs Windows; set {envVarName} instead.");

        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, blob);
    }

    public void ClearKey()
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    private string? ReadFile()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(filePath), Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Corrupt, or protected by another user/machine: treat as "no key" rather than crash.
            return null;
        }
    }
}
