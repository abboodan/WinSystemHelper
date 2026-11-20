using System.Security.AccessControl;
using System.Security.Principal;

namespace WinSystemHelper;

/// <summary>
/// Where the service is installed. The service runs as LocalSystem, so its executable
/// must sit in a folder ordinary users cannot write to; otherwise replacing the file
/// is a direct path to SYSTEM.
/// </summary>
public static class InstallLayout
{
    private const FileSystemRights WriteLikeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    // GENERIC_WRITE | GENERIC_ALL, as they appear in inherit-only entries.
    private const int GenericWriteLikeBits = 0x40000000 | 0x10000000;

    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ServiceConstants.ServiceName);

    /// <summary>A copy already under Program Files is installed in place; anything else is relocated.</summary>
    public static string ResolveInstallDirectory(string currentDirectory)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return IsWithin(currentDirectory, programFiles)
            ? TrimSeparators(Path.GetFullPath(currentDirectory))
            : DefaultInstallDirectory;
    }

    public static bool IsWithin(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string fullPath = TrimSeparators(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        string fullDirectory = TrimSeparators(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase) &&
            fullPath.Length > fullDirectory.Length;
    }

    /// <summary>
    /// Copies the application files. Configuration, logs and temporary files stay
    /// behind: the configuration is written separately with a restricted access list.
    /// </summary>
    public static int CopyApplicationFiles(string sourceDirectory, string targetDirectory)
    {
        string source = TrimSeparators(Path.GetFullPath(sourceDirectory));
        string target = TrimSeparators(Path.GetFullPath(targetDirectory));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (IsWithin(target, source))
        {
            throw new InvalidOperationException("The install folder cannot be inside the folder being installed.");
        }

        Directory.CreateDirectory(target);
        int copied = 0;

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(file);
            if (name.StartsWith(ServiceConstants.ConfigFileName, StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// True when an account other than SYSTEM, Administrators or TrustedInstaller owns
    /// the folder or is allowed to change its contents.
    /// </summary>
    public static bool IsWritableByNonAdministrators(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        DirectorySecurity security = new DirectoryInfo(directory)
            .GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
        {
            return true;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                IsTrusted(sid) ||
                sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid))
            {
                continue;
            }

            if ((rule.FileSystemRights & WriteLikeRights) != 0 ||
                ((int)rule.FileSystemRights & GenericWriteLikeBits) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTrusted(SecurityIdentifier sid)
    {
        return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.Equals(TrustedInstallerSid);
    }

    private static void CopyWithRetry(string source, string destination)
    {
        // A service that was just stopped can keep its executable locked for a moment.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 15)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static string TrimSeparators(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
