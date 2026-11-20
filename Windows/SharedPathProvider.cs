using System.Security.AccessControl;
using System.Security.Principal;

namespace WinSystemHelper;

/// <summary>
/// Service-private working folders. Everything here is executed or trusted by the
/// service (updater scripts, staged payloads, rollback backups), so it must never be
/// writable by ordinary users. Folders live under %ProgramData%\WinSystemHelper\Service
/// with an explicit SYSTEM/Administrators-only access list.
/// </summary>
public sealed class SharedPathProvider : ISharedPathProvider
{
    private readonly string _appRoot;
    private readonly string _serviceRoot;
    private readonly object _sync = new();
    private readonly HashSet<string> _secured = new(StringComparer.OrdinalIgnoreCase);
    private bool _rootPrepared;

    public SharedPathProvider()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ServiceConstants.ServiceName))
    {
    }

    public SharedPathProvider(string appRoot)
    {
        _appRoot = Path.GetFullPath(appRoot);
        _serviceRoot = Path.Combine(_appRoot, "Service");
    }

    /// <summary>Pre-1.6 location. World-writable; nothing read from it may be executed.</summary>
    public static string LegacyPublicRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
        ServiceConstants.ServiceName);

    public string GetSharedTempDirectory(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) ||
            purpose.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            purpose is "." or "..")
        {
            throw new ArgumentException("Purpose must be a simple folder name.", nameof(purpose));
        }

        string directory = Path.Combine(_serviceRoot, purpose);

        lock (_sync)
        {
            PrepareRoot();

            if (_secured.Contains(directory) && Directory.Exists(directory))
            {
                return directory;
            }

            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory);
            QueueStore.SecureDirectory(directory);
            _secured.Add(directory);
        }

        return directory;
    }

    private void PrepareRoot()
    {
        if (_rootPrepared && Directory.Exists(_serviceRoot))
        {
            return;
        }

        Directory.CreateDirectory(_appRoot);
        RejectReparsePoint(_appRoot);
        // Take the application root away from whoever may have pre-created it.
        QueueStore.SecureDirectory(_appRoot);

        if (Directory.Exists(_serviceRoot) &&
            (IsReparsePoint(_serviceRoot) || !HasTrustedOwner(_serviceRoot)))
        {
            // Planted before the service first ran: its contents cannot be trusted.
            Directory.Move(_serviceRoot, $"{_serviceRoot}.untrusted-{Guid.NewGuid():N}");
        }

        Directory.CreateDirectory(_serviceRoot);
        QueueStore.SecureDirectory(_serviceRoot);
        _secured.Clear();
        _rootPrepared = true;
    }

    private static void RejectReparsePoint(string directory)
    {
        if (IsReparsePoint(directory))
        {
            throw new IOException($"Refusing to use a redirected folder: {directory}");
        }
    }

    private static bool IsReparsePoint(string directory)
    {
        return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool HasTrustedOwner(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        IdentityReference? owner = new DirectoryInfo(directory)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier));

        return owner is SecurityIdentifier sid &&
            (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
             sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
             (WindowsIdentity.GetCurrent().User is { } currentUser && sid.Equals(currentUser)));
    }
}
