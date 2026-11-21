using Microsoft.Extensions.Logging;
using Xunit;

namespace WinSystemHelper.Tests;

public sealed class FileLogTests : TestWorkspace
{
    [Fact(DisplayName = "Service file logging uses the private directory")]
    public void ServiceFileLoggingUsesThePrivateDirectory()
    {
        Assert.True(PrivateStorage(nameof(ServiceFileLoggingUsesThePrivateDirectory)));
    }

    [Fact(DisplayName = "Unavailable private storage disables file diagnostics without falling back to another path")]
    public void UnavailablePrivateStorageDisablesFileDiagnosticsWithoutFallingBackToAnotherPath()
    {
        Assert.True(PrivateStorage(nameof(UnavailablePrivateStorageDisablesFileDiagnosticsWithoutFallingBackToAnotherPath)));
    }

    private bool PrivateStorage(string check)
    {
        var paths = new SharedPathProvider(Path.Combine(TestRoot, "log-app-root"));
        using var log = FileLogStore.CreateForService(paths);
        log.Write("INF", "Synthetic", "private log entry");
        bool observed = log.Folder is { } folder && OnlyTrustedAccess(folder) && log.ReadTail(1, 200).Contains("private log entry");
        if (check == nameof(ServiceFileLoggingUsesThePrivateDirectory))
        {
            return observed;
        }

        var blockedPaths = new SharedPathProvider(Path.Combine(TestRoot, "blocked-log-root"));
        string workingDirectory = blockedPaths.GetSharedTempDirectory("Updates");
        string blocked = Path.Combine(Path.GetDirectoryName(workingDirectory)!, "Logs");
        File.WriteAllText(blocked, "synthetic file");
        using var unavailable = FileLogStore.CreateForService(blockedPaths);
        unavailable.BeginRun();
        unavailable.RecordCrash("Synthetic", new IOException("simulated crash"));
        using MemoryStream snapshot = unavailable.CreateSnapshot();
        return unavailable.Folder is null && !unavailable.IsAvailable && snapshot.Length == 0 &&
            unavailable.ReadTail(1, 200).Length == 0 && File.ReadAllText(blocked) == "synthetic file";
    }

    [Fact(DisplayName = "Log lines never contain the bot token")]
    public void LogLinesNeverContainTheBotToken()
    {
        Assert.True(FileLog(nameof(LogLinesNeverContainTheBotToken)));
    }

    [Fact(DisplayName = "Framework noise is dropped but warnings and their exceptions are kept")]
    public void FrameworkNoiseIsDroppedButWarningsAndTheirExceptionsAreKept()
    {
        Assert.True(FileLog(nameof(FrameworkNoiseIsDroppedButWarningsAndTheirExceptionsAreKept)));
    }

    [Fact(DisplayName = "Log rotates and keeps a bounded number of files")]
    public void LogRotatesAndKeepsABoundedNumberOfFiles()
    {
        Assert.True(FileLog(nameof(LogRotatesAndKeepsABoundedNumberOfFiles)));
    }

    [Fact(DisplayName = "Tail returns the newest lines in order")]
    public void TailReturnsTheNewestLinesInOrder()
    {
        Assert.True(FileLog(nameof(TailReturnsTheNewestLinesInOrder)));
    }

    [Fact(DisplayName = "Tail is bounded for a chat message")]
    public void TailIsBoundedForAChatMessage()
    {
        Assert.True(FileLog(nameof(TailIsBoundedForAChatMessage)));
    }

    [Fact(DisplayName = "Log snapshot includes both rotated files and the current file in chronological order")]
    public void LogSnapshotIncludesBothRotatedFilesAndTheCurrentFileInChronologicalOrder()
    {
        Assert.True(FileLog(nameof(LogSnapshotIncludesBothRotatedFilesAndTheCurrentFileInChronologicalOrder)));
    }

    private bool FileLog(string check)
    {
        string logFolder = Path.Combine(TestRoot, "logs");
        using var log = new FileLogStore(logFolder, maxFileBytes: 2048, retainedFiles: 2);
        string syntheticToken = new string('7', 9) + ":" + new string('Q', 35);
        log.SetSecret("exact-secret-value");
        using (var provider = new FileLoggerProvider(log))
        {
            provider.CreateLogger("WinSystemHelper.Worker").LogInformation("Polling with {Token} and exact-secret-value", syntheticToken);
            provider.CreateLogger("System.Net.Http.HttpClient.Default").LogInformation("framework chatter");
            provider.CreateLogger("System.Net.Http.HttpClient.Default").LogWarning(new IOException("synthetic failure"), "framework warning");
        }
        string tail = log.ReadTail(10, 3500);
        bool observed = tail.Contains("[INF] Worker: Polling") && !tail.Contains(syntheticToken) && !tail.Contains("exact-secret-value") && tail.Contains("[bot-token]");
        if (check == nameof(LogLinesNeverContainTheBotToken))
        {
            return observed;
        }
        observed = !tail.Contains("framework chatter") && tail.Contains("framework warning") && tail.Contains("synthetic failure");
        if (check == nameof(FrameworkNoiseIsDroppedButWarningsAndTheirExceptionsAreKept))
        {
            return observed;
        }
        for (int i = 0; i < 60; i++) log.Write("INF", "Synthetic", $"line {i:D3} " + new string('x', 80));
        observed = System.IO.File.Exists(Path.Combine(logFolder, "service.1.log")) && new FileInfo(log.CurrentPath).Length < 4096 &&
            Directory.GetFiles(logFolder, "service*.log").Length <= 3;
        if (check == nameof(LogRotatesAndKeepsABoundedNumberOfFiles))
        {
            return observed;
        }
        string last = log.ReadTail(3, 3500);
        observed = last.Split('\n').Length == 3 && last.TrimEnd().EndsWith(new string('x', 80)) && last.Contains("line 059");
        if (check == nameof(TailReturnsTheNewestLinesInOrder))
        {
            return observed;
        }
        observed = log.ReadTail(200, 300).Length <= 300;
        if (check == nameof(TailIsBoundedForAChatMessage))
        {
            return observed;
        }
        string oldest = File.ReadLines(Path.Combine(logFolder, "service.2.log")).First();
        string previous = File.ReadLines(Path.Combine(logFolder, "service.1.log")).First();
        using var snapshot = new StreamReader(log.CreateSnapshot());
        string contents = snapshot.ReadToEnd();
        return contents.StartsWith(oldest, StringComparison.Ordinal) && contents.IndexOf(previous, StringComparison.Ordinal) > 0 &&
            contents.IndexOf(previous, StringComparison.Ordinal) < contents.IndexOf("line 059", StringComparison.Ordinal);
    }
}
