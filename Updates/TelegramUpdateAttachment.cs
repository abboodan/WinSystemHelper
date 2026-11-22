using System.IO.Compression;
using System.Text;

namespace WinSystemHelper;

internal static class TelegramUpdateAttachment
{
    private const string PackageEntryName = "WinSystemHelper-ota.zip";
    private const string SignatureEntryName = PackageEntryName + OtaSignature.SignatureFileSuffix;

    public static string Create(string packagePath)
    {
        if (!UpdatePackage.IsZipFileName(packagePath))
        {
            throw new ArgumentException("Select a signed OTA .zip package.", nameof(packagePath));
        }

        using FileStream package = File.OpenRead(packagePath);
        EnsureWithinTelegramLimit(package.Length);
        string signaturePath = packagePath + OtaSignature.SignatureFileSuffix;
        string? signature = File.Exists(signaturePath) ? File.ReadAllText(signaturePath).Trim() : null;
        if (!OtaSignature.Verify(packagePath, signature))
        {
            throw new InvalidOperationException("The package must match a valid release .sig file next to it.");
        }

        string attachmentPath = Path.ChangeExtension(packagePath, ".telegram.zip");
        string temporaryPath = Path.ChangeExtension(attachmentPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (ZipArchive archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                using (Stream packageEntry = archive.CreateEntry(PackageEntryName, CompressionLevel.NoCompression).Open())
                {
                    package.CopyTo(packageEntry);
                }

                using var signatureEntry = new StreamWriter(archive.CreateEntry(SignatureEntryName).Open());
                signatureEntry.Write(signature);
            }

            EnsureWithinTelegramLimit(new FileInfo(temporaryPath).Length);
            File.Move(temporaryPath, attachmentPath, overwrite: true);
            return attachmentPath;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static async Task<string> UnpackAsync(string attachmentPath, string packagePath, CancellationToken cancellationToken)
    {
        using FileStream input = File.OpenRead(attachmentPath);
        EnsureWithinTelegramLimit(input.Length);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        ZipArchiveEntry? packageEntry = archive.GetEntry(PackageEntryName);
        ZipArchiveEntry? signatureEntry = archive.GetEntry(SignatureEntryName);
        if (archive.Entries.Count != 2 || packageEntry is null || signatureEntry is null)
        {
            throw new InvalidOperationException(
                "Expected a signed Telegram update attachment. Use /ota-attachment to prepare it, " +
                "or attach the release ZIP with caption /update <signature>.");
        }

        string signature = await ReadSignatureAsync(signatureEntry, cancellationToken);
        EnsureWithinTelegramLimit(packageEntry.Length);
        await using Stream source = packageEntry.Open();
        await using FileStream destination = new(packagePath, FileMode.CreateNew, FileAccess.Write);
        await UpdatePackage.CopyStreamWithLimitAsync(source, destination, UpdatePackage.MaxTelegramUpdateDocumentBytes,
            totalBytesHint: null, progressCallback: null, cancellationToken);
        return signature;
    }

    private static async Task<string> ReadSignatureAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[OtaSignature.MaxSignatureTextLength + 1];
        await using Stream stream = entry.Open();
        int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        if (length == 0 || length == buffer.Length)
        {
            throw new InvalidOperationException("The Telegram attachment has an empty or oversized release signature.");
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static void EnsureWithinTelegramLimit(long length)
    {
        if (length > UpdatePackage.MaxTelegramUpdateDocumentBytes)
        {
            throw new InvalidOperationException("The Telegram update attachment exceeds the 20 MiB download limit.");
        }
    }
}
