using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WinSystemHelper;

public sealed class ConfigurationFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();

    public ConfigurationFileService() : this(AppContext.BaseDirectory)
    {
    }

    public ConfigurationFileService(string directory)
    {
        ConfigPath = Path.Combine(directory, ServiceConstants.ConfigFileName);
    }

    public string ConfigPath { get; }

    public AppConfiguration Load()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException(
                $"Missing {ConfigPath}. Run {ServiceConstants.ServiceName}.exe /install /token <bot-token> /chatid <admin-chat-id> first.",
                ConfigPath);
        }

        // Installs made before the file was protected are tightened on first start.
        TryRestrictAccess(ConfigPath);

        string json = File.ReadAllText(ConfigPath);
        AppConfiguration? configuration = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions);

        if (configuration is null ||
            string.IsNullOrWhiteSpace(configuration.BotToken) ||
            configuration.GetEffectiveAdminChatIds().Count == 0)
        {
            throw new InvalidOperationException($"{ConfigPath} is missing BotToken or AdminChatIds.");
        }

        if (configuration.OutboxMaxSizeMB is < 1 or > 65536)
            throw new InvalidOperationException("OutboxMaxSizeMB must be between 1 and 65536 (MiB).");
        return configuration;
    }

    public void Save(AppConfiguration configuration) => SaveAtomic(configuration);

    public void SaveAtomic(AppConfiguration configuration)
    {
        lock (_sync)
        {
            string tempPath = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
            string json = JsonSerializer.Serialize(configuration, JsonOptions);

            try
            {
                // Create the file empty, lock it down, and only then write the token,
                // so the secret never exists on disk with the folder's default access.
                using (new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                RestrictAccess(tempPath);
                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempPath, ConfigPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// Limits the file to SYSTEM, Administrators and the account that wrote it.
    /// The bot token is stored in clear text, so this access list is what protects it.
    /// </summary>
    public static void RestrictAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        var allowed = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        // An elevated installer already has access through Administrators. Naming the
        // account itself would let that user's non-elevated programs read the token.
        bool elevated = current.IsSystem ||
            new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator);
        if (!elevated)
        {
            allowed.Add(current.User!);
        }

        foreach (SecurityIdentifier sid in allowed)
        {
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(acl);
    }

    private static void TryRestrictAccess(string path)
    {
        try
        {
            RestrictAccess(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Reading the configuration still matters more than failing to start.
        }
    }
}
