using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Telegram.Bot;
using WinSystemHelper;

const string ServiceName = "WinSystemHelper";
string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

return await RunAsync(args);

async Task<int> RunAsync(string[] launchArgs)
{
    try
    {
        if (launchArgs.Length > 0)
        {
            return await RunSilentCliModeAsync(launchArgs);
        }

        if (Environment.UserInteractive)
        {
            return await RunInteractiveModeAsync();
        }

        await RunServiceModeAsync();
        return 0;
    }
    catch (Exception ex)
    {
        if (Environment.UserInteractive)
        {
            Console.Error.WriteLine(ex.Message);
        }

        return 1;
    }
}

async Task<int> RunSilentCliModeAsync(string[] launchArgs)
{
    CliOptions options = ParseArguments(launchArgs);

    if (options.Uninstall)
    {
        if (!EnsureAdministrator())
        {
            return 1;
        }

        await UninstallServiceAsync(writeOutput: false);
        return 0;
    }

    if (!options.Install)
    {
        Console.Error.WriteLine("Unknown command. Use /install or /uninstall.");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(options.BotToken) || options.AdminChatId is null)
    {
        Console.Error.WriteLine("Install requires /token <bot-token> and /chatid <admin-chat-id>.");
        return 1;
    }

    if (!EnsureAdministrator())
    {
        return 1;
    }

    SaveConfiguration(new AppConfiguration(options.BotToken, options.AdminChatId.Value));
    await InstallServiceAsync(writeOutput: false);

    return 0;
}

async Task<int> RunInteractiveModeAsync()
{
    Console.Title = ServiceName;
    Console.WriteLine($"{ServiceName} setup");
    Console.WriteLine($"Configuration path: {configPath}");
    Console.WriteLine();

    if (await IsServiceInstalledAsync())
    {
        Console.WriteLine("Service is installed. Press 'U' to uninstall or 'Esc' to exit.");

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.U)
            {
                if (!EnsureAdministrator())
                {
                    return 1;
                }

                await UninstallServiceAsync(writeOutput: true);
                return 0;
            }

            if (key.Key == ConsoleKey.Escape)
            {
                return 0;
            }
        }
    }

    if (!EnsureAdministrator())
    {
        return 1;
    }

    Console.Write("Telegram Bot Token: ");
    string? botToken = Console.ReadLine();

    Console.Write("Admin Chat ID: ");
    string? chatIdText = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(botToken) || !long.TryParse(chatIdText, out long adminChatId))
    {
        Console.Error.WriteLine("Invalid bot token or chat ID.");
        return 1;
    }

    SaveConfiguration(new AppConfiguration(botToken.Trim(), adminChatId));
    await InstallServiceAsync(writeOutput: true);

    return 0;
}

async Task RunServiceModeAsync()
{
    AppConfiguration configuration = LoadConfiguration();

    IHost host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = ServiceName;
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton(configuration);
            services.AddHttpClient();
            services.AddSingleton<ITelegramBotClient>(_ =>
                new TelegramBotClient(configuration.BotToken));
            services.AddHostedService<Worker>();
        })
        .Build();

    await host.RunAsync();
}

async Task InstallServiceAsync(bool writeOutput)
{
    if (!EnsureAdministrator())
    {
        return;
    }

    string executablePath = GetExecutablePath();

    if (await IsServiceInstalledAsync())
    {
        await UninstallServiceAsync(writeOutput);
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    ProcessResult create = await RunProcessAsync(
        "sc.exe",
        $"create {ServiceName} binPath= \"{EscapeArgument(executablePath)}\" start= auto");

    EnsureSuccess(create, "Failed to create Windows Service.");

    ProcessResult start = await RunProcessAsync("sc.exe", $"start {ServiceName}");
    EnsureSuccess(start, "Failed to start Windows Service.");

    if (writeOutput)
    {
        Console.WriteLine("Service installed and started.");
    }
}

async Task UninstallServiceAsync(bool writeOutput)
{
    if (!EnsureAdministrator())
    {
        return;
    }

    if (!await IsServiceInstalledAsync())
    {
        if (writeOutput)
        {
            Console.WriteLine("Service is not installed.");
        }

        return;
    }

    await RunProcessAsync("sc.exe", $"stop {ServiceName}");
    await Task.Delay(TimeSpan.FromSeconds(1));

    ProcessResult delete = await RunProcessAsync("sc.exe", $"delete {ServiceName}");
    EnsureSuccess(delete, "Failed to delete Windows Service.");

    if (writeOutput)
    {
        Console.WriteLine("Service stopped and deleted.");
    }
}

async Task<bool> IsServiceInstalledAsync()
{
    ProcessResult query = await RunProcessAsync("sc.exe", $"query {ServiceName}");
    return query.ExitCode == 0;
}

void SaveConfiguration(AppConfiguration configuration)
{
    string json = JsonSerializer.Serialize(configuration, jsonOptions);
    File.WriteAllText(configPath, json);
}

AppConfiguration LoadConfiguration()
{
    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException(
            $"Missing {configPath}. Run {ServiceName}.exe /install /token <bot-token> /chatid <admin-chat-id> first.",
            configPath);
    }

    string json = File.ReadAllText(configPath);
    AppConfiguration? configuration = JsonSerializer.Deserialize<AppConfiguration>(json, jsonOptions);

    if (configuration is null ||
        string.IsNullOrWhiteSpace(configuration.BotToken) ||
        configuration.AdminChatId == 0)
    {
        throw new InvalidOperationException($"{configPath} is missing BotToken or AdminChatId.");
    }

    return configuration;
}

static bool EnsureAdministrator()
{
    if (IsRunningAsAdministrator())
    {
        return true;
    }

    WriteErrorInRed("Access Denied: Please run this application as Administrator to install/uninstall the service.");
    return false;
}

static bool IsRunningAsAdministrator()
{
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    WindowsPrincipal principal = new(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void WriteErrorInRed(string message)
{
    ConsoleColor originalColor = Console.ForegroundColor;

    try
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
    }
    finally
    {
        Console.ForegroundColor = originalColor;
    }
}

static CliOptions ParseArguments(string[] launchArgs)
{
    CliOptions options = new();

    for (int i = 0; i < launchArgs.Length; i++)
    {
        string current = launchArgs[i];
        string normalized = current.TrimStart('/', '-').ToLowerInvariant();

        if (normalized == "install")
        {
            options.Install = true;
            continue;
        }

        if (normalized == "uninstall")
        {
            options.Uninstall = true;
            continue;
        }

        if (TryReadOptionValue(current, "token", launchArgs, ref i, out string? token))
        {
            options.BotToken = token;
            continue;
        }

        if (TryReadOptionValue(current, "chatid", launchArgs, ref i, out string? chatIdText) &&
            long.TryParse(chatIdText, out long chatId))
        {
            options.AdminChatId = chatId;
        }
    }

    return options;
}

static bool TryReadOptionValue(
    string current,
    string name,
    string[] args,
    ref int index,
    out string? value)
{
    value = null;
    string prefix = current.StartsWith('/') || current.StartsWith('-')
        ? current[1..]
        : current;

    if (prefix.StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase) ||
        prefix.StartsWith($"{name}=", StringComparison.OrdinalIgnoreCase))
    {
        value = prefix[(name.Length + 1)..];
        return true;
    }

    if (!prefix.Equals(name, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (index + 1 >= args.Length)
    {
        return true;
    }

    index++;
    value = args[index];
    return true;
}

static string GetExecutablePath()
{
    return Environment.ProcessPath ??
        Process.GetCurrentProcess().MainModule?.FileName ??
        throw new InvalidOperationException("Unable to resolve executable path.");
}

static string EscapeArgument(string value)
{
    return value.Replace("\"", "\\\"", StringComparison.Ordinal);
}

static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments)
{
    using Process process = new()
    {
        StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        }
    };

    process.Start();

    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync();

    return new ProcessResult(
        process.ExitCode,
        await outputTask,
        await errorTask);
}

static void EnsureSuccess(ProcessResult result, string message)
{
    if (result.ExitCode == 0)
    {
        return;
    }

    string details = string.Join(
        Environment.NewLine,
        message,
        result.StandardOutput.Trim(),
        result.StandardError.Trim()).Trim();

    throw new InvalidOperationException(details);
}

internal sealed class CliOptions
{
    public bool Install { get; set; }
    public bool Uninstall { get; set; }
    public string? BotToken { get; set; }
    public long? AdminChatId { get; set; }
}

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
