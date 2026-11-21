using Xunit;

namespace WinSystemHelper.Tests;

public sealed class InstallationTests : TestWorkspace
{
    [Fact(DisplayName = "Service working folders are private to SYSTEM and administrators")]
    public async Task ServiceWorkingFoldersArePrivateToSYSTEMAndAdministrators()
    {
        Assert.True(await WorkingFoldersAsync(nameof(ServiceWorkingFoldersArePrivateToSYSTEMAndAdministrators)));
    }

    [Fact(DisplayName = "Updater scripts inherit no access for ordinary users")]
    public async Task UpdaterScriptsInheritNoAccessForOrdinaryUsers()
    {
        Assert.True(await WorkingFoldersAsync(nameof(UpdaterScriptsInheritNoAccessForOrdinaryUsers)));
    }

    [Fact(DisplayName = "Service folder names cannot leave the private root")]
    public async Task ServiceFolderNamesCannotLeaveThePrivateRoot()
    {
        Assert.True(await WorkingFoldersAsync(nameof(ServiceFolderNamesCannotLeaveThePrivateRoot)));
    }

    [Fact(DisplayName = "Legacy public folder is distinct from private storage")]
    public async Task LegacyPublicFolderIsDistinctFromPrivateStorage()
    {
        Assert.True(await WorkingFoldersAsync(nameof(LegacyPublicFolderIsDistinctFromPrivateStorage)));
    }

    private async Task<bool> WorkingFoldersAsync(string check)
    {
        var paths = new SharedPathProvider(Path.Combine(TestRoot, "app-root"));
        string updates = paths.GetSharedTempDirectory("Updates");
        bool observed = updates.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase) && OnlyTrustedAccess(updates) &&
            OnlyTrustedAccess(Path.GetDirectoryName(updates)!);
        if (check == nameof(ServiceWorkingFoldersArePrivateToSYSTEMAndAdministrators))
        {
            return observed;
        }
        string script = Path.Combine(updates, "synthetic.ps1");
        await System.IO.File.WriteAllTextAsync(script, "exit 0");
        var scriptSecurity = new FileInfo(script).GetAccessControl();
        bool leaked = false;
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in scriptSecurity.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
            if (sid.IsWellKnown(System.Security.Principal.WellKnownSidType.InteractiveSid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid) ||
                sid.IsWellKnown(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid) || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.WorldSid)) leaked = true;
        }
        observed = !leaked;
        if (check == nameof(UpdaterScriptsInheritNoAccessForOrdinaryUsers))
        {
            return observed;
        }
        bool escaped = true;
        try { paths.GetSharedTempDirectory("..\\escape"); } catch (ArgumentException) { escaped = false; }
        observed = !escaped;
        if (check == nameof(ServiceFolderNamesCannotLeaveThePrivateRoot))
        {
            return observed;
        }
        return !SharedPathProvider.LegacyPublicRoot.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Configuration file is restricted to SYSTEM, administrators and its writer")]
    public void ConfigurationFileIsRestrictedToSYSTEMAdministratorsAndItsWriter()
    {
        Assert.True(ConfigurationStorage(nameof(ConfigurationFileIsRestrictedToSYSTEMAdministratorsAndItsWriter)));
    }

    [Fact(DisplayName = "Configuration writes leave no temporary copies")]
    public void ConfigurationWritesLeaveNoTemporaryCopies()
    {
        Assert.True(ConfigurationStorage(nameof(ConfigurationWritesLeaveNoTemporaryCopies)));
    }

    private bool ConfigurationStorage(string check)
    {
        string configDirectory = Path.Combine(TestRoot, "config");
        Directory.CreateDirectory(configDirectory);
        var service = new ConfigurationFileService(configDirectory);
        service.Save(new AppConfiguration { BotToken = "1:synthetic", AdminChatIds = [1] });
        service.SaveAtomic(new AppConfiguration { BotToken = "1:synthetic", AdminChatIds = [1, 2] });
        bool observed = OnlyTrustedAccess(service.ConfigPath) && service.Load().AdminChatIds.Length == 2;
        if (check == nameof(ConfigurationFileIsRestrictedToSYSTEMAdministratorsAndItsWriter))
        {
            return observed;
        }
        return Directory.GetFiles(configDirectory).Length == 1;
    }

    [Fact(DisplayName = "Install copy leaves configuration and logs behind")]
    public async Task InstallCopyLeavesConfigurationAndLogsBehind()
    {
        Assert.True(await CopyAndLayoutAsync(nameof(InstallCopyLeavesConfigurationAndLogsBehind)));
    }

    [Fact(DisplayName = "Install relocates to Program Files unless already inside it")]
    public async Task InstallRelocatesToProgramFilesUnlessAlreadyInsideIt()
    {
        Assert.True(await CopyAndLayoutAsync(nameof(InstallRelocatesToProgramFilesUnlessAlreadyInsideIt)));
    }

    [Fact(DisplayName = "Install folders writable by ordinary users are detected")]
    public async Task InstallFoldersWritableByOrdinaryUsersAreDetected()
    {
        Assert.True(await CopyAndLayoutAsync(nameof(InstallFoldersWritableByOrdinaryUsersAreDetected)));
    }

    private async Task<bool> CopyAndLayoutAsync(string check)
    {
        string source = Path.Combine(TestRoot, "install-source"), target = Path.Combine(TestRoot, "install-target");
        Directory.CreateDirectory(Path.Combine(source, "runtimes"));
        foreach (string name in new[] { "app.exe", "config.json", "config.json.bak", "setup.log", Path.Combine("runtimes", "native.dll") })
            await System.IO.File.WriteAllTextAsync(Path.Combine(source, name), "synthetic");
        int copied = InstallLayout.CopyApplicationFiles(source, target);
        bool observed = copied == 2 && System.IO.File.Exists(Path.Combine(target, "runtimes", "native.dll")) &&
            Directory.GetFiles(target).Length == 1;
        if (check == nameof(InstallCopyLeavesConfigurationAndLogsBehind))
        {
            return observed;
        }
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string custom = Path.Combine(programFiles, "Custom", "Tool");
        observed = InstallLayout.ResolveInstallDirectory(source) == InstallLayout.DefaultInstallDirectory &&
            InstallLayout.ResolveInstallDirectory(custom + Path.DirectorySeparatorChar) == custom &&
            InstallLayout.ResolveInstallDirectory(programFiles + "Evil") == InstallLayout.DefaultInstallDirectory;
        if (check == nameof(InstallRelocatesToProgramFilesUnlessAlreadyInsideIt))
        {
            return observed;
        }
        return InstallLayout.IsWritableByNonAdministrators(Path.GetTempPath()) && !InstallLayout.IsWritableByNonAdministrators(programFiles);
    }
}
