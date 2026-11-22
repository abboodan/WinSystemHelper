using System.Security.Cryptography;

namespace WinSystemHelper;

/// <summary>
/// Detached ECDSA P-256 / SHA-256 signature over the OTA ZIP.
///
/// An update is code that will run as LocalSystem. Without a signature the only
/// thing standing between "can send a command" and "can run anything" is the
/// Telegram chat; with one, a package must also come from whoever holds the
/// release key, which never leaves the release pipeline.
/// </summary>
public static class OtaSignature
{
    /// <summary>
    /// Base64 SubjectPublicKeyInfo of the release signing key, printed by
    /// <c>WinSystemHelper.exe /ota-keygen</c>. While this is empty the build cannot
    /// verify packages and accepts unsigned updates (reported as such to the admin).
    /// Once set, every OTA package must carry a valid signature.
    /// </summary>
    public const string PinnedPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMCSyYaG0W6cI8KhfVSAOcCNVd5toysuhifpPBAFAPZhgcTxD4UHg7vfOETvub9N6xTH/yMCP5mEhB/Z7oazetg==";

    public const string SignatureFileSuffix = ".sig";

    /// <summary>Environment variable the release pipeline uses to pass the private key.</summary>
    public const string PrivateKeyEnvironmentVariable = "OTA_SIGNING_KEY";

    internal const int MaxSignatureTextLength = 512;

    public static bool IsEnforced => !string.IsNullOrWhiteSpace(PinnedPublicKey);

    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static string Sign(string filePath, string privateKeyBase64)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64.Trim()), out _);
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToBase64String(key.SignData(stream, HashAlgorithmName.SHA256));
    }

    /// <summary>Verifies against the pinned key. Any malformed input is simply "not valid".</summary>
    public static bool Verify(string filePath, string? signatureText)
    {
        return IsEnforced && Verify(filePath, signatureText, PinnedPublicKey);
    }

    public static bool Verify(string filePath, string? signatureText, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureText) ||
            signatureText.Length > MaxSignatureTextLength ||
            string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return false;
        }

        try
        {
            byte[] signature = Convert.FromBase64String(signatureText.Trim());
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            using FileStream stream = File.OpenRead(filePath);
            return key.VerifyData(stream, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or IOException)
        {
            return false;
        }
    }
}
