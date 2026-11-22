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
        if (launchArgs.Length == 1 && launchArgs[0] == "/queue-storage-check")
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            Console.WriteLine($"SQLite storage available: {command.ExecuteScalar()}");
            return 0;
        }
        if (launchArgs.Length > 0 && launchArgs[0].StartsWith("/ota-", StringComparison.OrdinalIgnoreCase))
        {
            return RunOtaSigningTool(launchArgs);
        }
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

// Release tooling. The private key is only ever read from a file or environment
// variable supplied by the maintainer; it is never printed or embedded.
//   /ota-keygen <private-key-file>   create a signing key pair
//   /ota-sign <package.zip>          write <package.zip>.sig using OTA_SIGNING_KEY
//   /ota-verify <package.zip>        exit 0 = valid, 1 = invalid, 3 = build has no pinned key
//   /ota-attachment <package.zip>    bundle a signed package for upload to Telegram
static int RunOtaSigningTool(string[] launchArgs)
{
    string mode = launchArgs[0].ToLowerInvariant();
    if (launchArgs.Length != 2)
    {
        Console.Error.WriteLine("Usage: /ota-keygen <private-key-file> | /ota-sign <package.zip> | /ota-verify <package.zip> | /ota-attachment <package.zip>");
        return 2;
    }

    string path = Path.GetFullPath(launchArgs[1]);
    string signaturePath = path + OtaSignature.SignatureFileSuffix;

    switch (mode)
    {
        case "/ota-keygen":
        {
            if (File.Exists(path))
            {
                Console.Error.WriteLine("Refusing to overwrite an existing key file.");
                return 1;
            }

            (string privateKey, string publicKey) = OtaSignature.GenerateKeyPair();
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            ConfigurationFileService.RestrictAccess(path);
            File.WriteAllText(path, privateKey);
            Console.WriteLine($"Private key written to: {path}");
            Console.WriteLine("Keep it out of the repository. Store its content as the GitHub Actions secret");
            Console.WriteLine($"{OtaSignature.PrivateKeyEnvironmentVariable}, then delete or back up the file offline.");
            Console.WriteLine();
            Console.WriteLine("Public key (set OtaSignature.PinnedPublicKey to this value):");
            Console.WriteLine(publicKey);
            return 0;
        }
        case "/ota-sign":
        {
            string? privateKey = Environment.GetEnvironmentVariable(OtaSignature.PrivateKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(privateKey))
            {
                Console.Error.WriteLine($"{OtaSignature.PrivateKeyEnvironmentVariable} is not set.");
                return 1;
            }

            string signature = OtaSignature.Sign(path, privateKey);
            File.WriteAllText(signaturePath, signature);
            Console.WriteLine($"Signature written to: {signaturePath}");
            return 0;
        }
        case "/ota-verify":
        {
            if (!OtaSignature.IsEnforced)
            {
                Console.WriteLine("This build has no pinned release key; packages are not verified.");
                return 3;
            }

            bool valid = File.Exists(signaturePath) &&
                OtaSignature.Verify(path, File.ReadAllText(signaturePath));
            Console.WriteLine(valid ? "Signature valid." : "Signature missing or invalid.");
            return valid ? 0 : 1;
        }
        case "/ota-attachment":
        {
            string attachmentPath = TelegramUpdateAttachment.Create(path);
            Console.WriteLine($"Telegram attachment written to: {attachmentPath}");
            return 0;
        }
        default:
            Console.Error.WriteLine("Unknown release tool command.");
            return 2;
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

    await InstallServiceAsync(new AppConfiguration
    {
        BotToken = options.BotToken.Trim(),
        AdminChatIds = [options.AdminChatId.Value]
    }, writeOutput: false);

    return 0;
}

async Task<int> RunFirstRunSetupWizardAsync()
{
    Console.Title = ServiceName;
    Console.WriteLine("Welcome to WinSystemHelper Setup");
    Console.WriteLine($"Install folder: {InstallLayout.ResolveInstallDirectory(AppContext.BaseDirectory)}");
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

    await InstallServiceAsync(new AppConfiguration
    {
        BotToken = botToken.Trim(),
        AdminChatIds = adminChatIds.ToArray()
    }, writeOutput: true);

    return 0;
}

async Task RunServiceModeAsync()
{
    AppConfiguration configuration = LoadConfiguration();

    var sharedPaths = new SharedPathProvider();
    using FileLogStore logStore = FileLogStore.CreateForService(sharedPaths);
    logStore.SetSecret(configuration.BotToken);

    // Last chance to leave a trace before the process disappears.
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        logStore.RecordCrash("UnhandledException", e.ExceptionObject as Exception);
    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        logStore.Write("ERR", "UnobservedTask", "A background task failed without being awaited.", e.Exception);
        e.SetObserved();
    };

    IHost host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options =>
        {
            options.ServiceName = ServiceName;
        })
        .ConfigureLogging(logging => logging.AddProvider(new FileLoggerProvider(logStore)))
        .ConfigureServices(services =>
        {
            // A failed background service must not be stopped "cleanly": CrashGuard
            // records the cause and exits with a failure code so Windows restarts it.
            services.Configure<HostOptions>(options =>
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            services.AddSingleton(logStore);
            services.AddSingleton(configuration);
            services.AddSingleton(configurationFileService);
            services.AddHttpClient();
            services.AddSingleton<ITelegramBotClient>(_ =>
                new TelegramBotClient(configuration.BotToken));
            services.AddSingleton<TelegramMenuService>();
            services.AddSingleton<QueueStore>();
            services.AddSingleton<IOutboxTransport, TelegramOutboxTransport>();
            services.AddSingleton<TelegramNotifier>();
            services.AddSingleton<CooldownService>();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<ISharedPathProvider>(sharedPaths);
            services.AddSingleton<IWindowsSessionProcessRunner, WindowsSessionProcessRunner>();
            services.AddHostedService<OutboxSender>();
            services.AddHostedService<Worker>();
            // Must stay last: it watches the services registered before it.
            services.AddHostedService<CrashGuard>();
        })
        .Build();

    await host.RunAsync();
}

async Task InstallServiceAsync(AppConfiguration configuration, bool writeOutput)
{
    if (!EnsureAdministrator())
    {
        return;
    }

    // The service runs as LocalSystem, so it is installed where only administrators
    // can replace its files, and the configuration is written with a restricted
    // access list instead of inheriting the launch folder's permissions.
    string installDirectory = InstallLayout.ResolveInstallDirectory(AppContext.BaseDirectory);
    string executablePath = Path.Combine(installDirectory, Path.GetFileName(GetExecutablePath()));

    if (await IsServiceInstalledAsync())
    {
        await UninstallServiceAsync(writeOutput);
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    int copied = InstallLayout.CopyApplicationFiles(AppContext.BaseDirectory, installDirectory);
    new ConfigurationFileService(installDirectory).Save(configuration);

    if (writeOutput)
    {
        Console.WriteLine(copied > 0
            ? $"Installed {copied} file(s) to {installDirectory}."
            : $"Installing in place: {installDirectory}.");
    }

    // The path is stored quoted: an unquoted service path containing spaces lets
    // Windows try other executables first (for example C:\Program.exe).
    ProcessResult create = await RunProcessWithArgumentsAsync(
        "sc.exe",
        "create", ServiceName, "binPath=", $"\"{executablePath}\"", "start=", "auto");

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
        return MicRecorderFailure.MissingOutputPath;
    }

    MicRecordingStage stage = MicRecordingStage.PrepareOutput;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long maxAudioBytes = seconds * 32000L;
        long remaining = maxAudioBytes;

        using (var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 })
        {
            await using var writer = new WaveFileWriter(outputPath, waveIn.WaveFormat);
            waveIn.DataAvailable += (_, e) =>
            {
                int count = (int)Math.Min(e.BytesRecorded, remaining);
                if (count > 0)
                {
                    writer.Write(e.Buffer, 0, count);
                    remaining -= count;
                }
            };
            waveIn.RecordingStopped += (_, e) => recordingStopped.TrySetResult(e.Exception);

            stage = MicRecordingStage.OpenMicrophone;
            waveIn.StartRecording();
            stage = MicRecordingStage.RecordAudio;
            await Task.WhenAny(recordingStopped.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            if (!recordingStopped.Task.IsCompleted)
            {
                waveIn.StopRecording();
            }

            if (await recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(5)) is { } recordingException)
            {
                throw recordingException;
            }

            stage = MicRecordingStage.SaveOutput;
        }

        if (remaining == maxAudioBytes)
        {
            return MicRecorderFailure.NoAudioSamples;
        }

        await using (var completed = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            completed.Flush(true);
        }
        await File.WriteAllTextAsync(outputPath + ".complete", "complete");
        return 0;
    }
    catch (Exception ex)
    {
        return MicRecorderFailure.Encode(stage, ex);
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

static Task<ProcessResult> RunProcessAsync(string fileName, string arguments)
{
    return RunStartInfoAsync(new ProcessStartInfo(fileName, arguments));
}

static Task<ProcessResult> RunProcessWithArgumentsAsync(string fileName, params string[] arguments)
{
    // ArgumentList quotes and escapes each value, including embedded quotes.
    ProcessStartInfo startInfo = new(fileName);
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    return RunStartInfoAsync(startInfo);
}

static async Task<ProcessResult> RunStartInfoAsync(ProcessStartInfo startInfo)
{
    startInfo.CreateNoWindow = true;
    startInfo.RedirectStandardError = true;
    startInfo.RedirectStandardOutput = true;
    startInfo.UseShellExecute = false;

    using Process process = new() { StartInfo = startInfo };

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
