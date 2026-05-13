using System.Diagnostics;
using System.Security.Principal;
using NAudio.Wave;
using Telegram.Bot;
using WinSystemHelper;

const string ServiceName = ServiceConstants.ServiceName;
ConfigurationFileService configurationFileService = new();
string configPath = configurationFileService.ConfigPath;

return await RunAsync(args);

async Task<int> RunAsync(string[] launchArgs)
{
    try
    {
        if (IsMicRecorderHelperMode(launchArgs))
        {
            return await RunMicRecorderHelperAsync(launchArgs);
        }

        if (launchArgs.Length > 0)
        {
            return await RunSilentCliModeAsync(launchArgs);
        }

        if (File.Exists(configPath))
        {
            await RunServiceModeAsync();
            return 0;
        }

        if (Environment.UserInteractive)
        {
            return await RunFirstRunSetupWizardAsync();
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

    SaveConfiguration(new AppConfiguration
    {
        BotToken = options.BotToken.Trim(),
        AdminChatIds = [options.AdminChatId.Value]
    });
    await InstallServiceAsync(writeOutput: false);

    return 0;
}

async Task<int> RunFirstRunSetupWizardAsync()
{
    Console.Title = ServiceName;
    Console.WriteLine("Welcome to WinSystemHelper Setup");
    Console.WriteLine($"Configuration path: {configPath}");
    Console.WriteLine();

    if (!EnsureAdministrator())
    {
        return 1;
    }

    Console.Write("Telegram Bot Token: ");
    string? botToken = Console.ReadLine();

    Console.Write("Primary Admin Chat ID: ");
    string? chatIdText = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(botToken) || !long.TryParse(chatIdText, out long adminChatId))
    {
        Console.Error.WriteLine("Invalid bot token or chat ID.");
        return 1;
    }

    List<long> adminChatIds = [adminChatId];

    while (true)
    {
        Console.Write("Do you want to add another Admin Chat ID? (y/n): ");
        string? answer = Console.ReadLine()?.Trim();

        if (answer?.Equals("n", StringComparison.OrdinalIgnoreCase) == true)
        {
            break;
        }

        if (answer?.Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("Please enter 'y' or 'n'.");
            continue;
        }

        Console.Write("Additional Admin Chat ID: ");
        string? additionalChatIdText = Console.ReadLine();

        if (!long.TryParse(additionalChatIdText, out long additionalChatId) || additionalChatId == 0)
        {
            Console.WriteLine("Invalid chat ID. Try again.");
            continue;
        }

        if (adminChatIds.Contains(additionalChatId))
        {
            Console.WriteLine("That admin chat ID is already configured.");
            continue;
        }

        adminChatIds.Add(additionalChatId);
    }

    SaveConfiguration(new AppConfiguration
    {
        BotToken = botToken.Trim(),
        AdminChatIds = adminChatIds.ToArray()
    });
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
            services.AddSingleton(configurationFileService);
            services.AddHttpClient();
            services.AddSingleton<ITelegramBotClient>(_ =>
                new TelegramBotClient(configuration.BotToken));
            services.AddSingleton<TelegramMenuService>();
            services.AddSingleton<TelegramNotifier>();
            services.AddSingleton<CooldownService>();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<ISharedPathProvider, SharedPathProvider>();
            services.AddSingleton<IWindowsSessionProcessRunner, WindowsSessionProcessRunner>();
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
    configurationFileService.Save(configuration);
}

AppConfiguration LoadConfiguration()
{
    return configurationFileService.Load();
}

static bool IsMicRecorderHelperMode(string[] launchArgs)
{
    return launchArgs.Length > 0 &&
        launchArgs[0].TrimStart('/', '-').Equals("recordmic-helper", StringComparison.OrdinalIgnoreCase);
}

static async Task<int> RunMicRecorderHelperAsync(string[] launchArgs)
{
    int seconds = 10;
    string? outputPath = null;

    for (int i = 1; i < launchArgs.Length; i++)
    {
        if (TryReadOptionValue(launchArgs[i], "seconds", launchArgs, ref i, out string? secondsText) &&
            int.TryParse(secondsText, out int parsedSeconds))
        {
            seconds = Math.Clamp(parsedSeconds, 1, 60);
            continue;
        }

        if (TryReadOptionValue(launchArgs[i], "out", launchArgs, ref i, out string? parsedOutputPath))
        {
            outputPath = parsedOutputPath;
        }
    }

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        return 2;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await RecordWavAsync(outputPath, seconds, CancellationToken.None);

    return 0;
}

static async Task RecordWavAsync(
    string outputPath,
    int durationSeconds,
    CancellationToken cancellationToken)
{
    TaskCompletionSource<Exception?> recordingStopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    using WaveInEvent waveIn = new()
    {
        WaveFormat = new WaveFormat(16000, 16, 1),
        BufferMilliseconds = 100
    };

    await using WaveFileWriter writer = new(outputPath, waveIn.WaveFormat);

    waveIn.DataAvailable += (_, e) =>
    {
        if (e.BytesRecorded > 0)
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            writer.Flush();
        }
    };

    waveIn.RecordingStopped += (_, e) =>
    {
        recordingStopped.TrySetResult(e.Exception);
    };

    waveIn.StartRecording();

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellationToken);
    }
    finally
    {
        waveIn.StopRecording();
    }

    Exception? recordingException =
        await recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

    if (recordingException is not null)
    {
        throw recordingException;
    }
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
