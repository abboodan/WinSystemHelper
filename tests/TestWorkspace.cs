using WinSystemHelper;

namespace WinSystemHelper.Tests;

public abstract class TestWorkspace : IDisposable
{
    private static readonly string WorkspaceRoot = Path.Combine(Path.GetTempPath(), "WinSystemHelper-queue-tests");

    protected string TestRoot { get; } = CreateWorkspace();

    protected static string CreateWorkspace()
    {
        string path = Path.Combine(WorkspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    protected static void DeleteWorkspace(string path)
    {
        string safeRoot = Path.GetFullPath(WorkspaceRoot) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(path).StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(path, true);
        }
    }

    public void Dispose() => DeleteWorkspace(TestRoot);

    protected static bool OnlyTrustedAccess(string path)
    {
        System.Security.AccessControl.FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        if (!security.AreAccessRulesProtected || rules.Count == 0) return false;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
        {
            var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
            if (!sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) &&
                !sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) &&
                !sid.Equals(current.User!)) return false;
        }
        return true;
    }
    protected QueueStore Store(string name, Func<int>? limit = null) => new(Path.Combine(TestRoot, name), limit ?? (() => 512));
    protected static async Task<CaptureReservation> Capture(QueueStore store, int size = 32768, bool commit = true)
    {
        var item = await store.ReserveAsync(1, "voice", size + 128, 1, null) ?? throw new Exception("Reservation rejected");
        await File.WriteAllBytesAsync(item.Path, new byte[size]);
        await File.WriteAllTextAsync(item.Path + ".complete", "complete");
        if (commit) await store.CommitCaptureAsync(item);
        return item;
    }
}
