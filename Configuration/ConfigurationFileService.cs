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

    public string ConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, ServiceConstants.ConfigFileName);

    public AppConfiguration Load()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException(
                $"Missing {ConfigPath}. Run {ServiceConstants.ServiceName}.exe /install /token <bot-token> /chatid <admin-chat-id> first.",
                ConfigPath);
        }

        string json = File.ReadAllText(ConfigPath);
        AppConfiguration? configuration = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions);

        if (configuration is null ||
            string.IsNullOrWhiteSpace(configuration.BotToken) ||
            configuration.GetEffectiveAdminChatIds().Count == 0)
        {
            throw new InvalidOperationException($"{ConfigPath} is missing BotToken or AdminChatIds.");
        }

        return configuration;
    }

    public void Save(AppConfiguration configuration)
    {
        string json = JsonSerializer.Serialize(configuration, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public void SaveAtomic(AppConfiguration configuration)
    {
        lock (_sync)
        {
            string tempPath = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
            string json = JsonSerializer.Serialize(configuration, JsonOptions);

            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
    }
}
