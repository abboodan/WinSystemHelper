using System.Text;
using System.Text.RegularExpressions;

namespace WinSystemHelper;

/// <summary>
/// A small rotating log file in the service's private storage. It exists so a
/// problem can be traced remotely: the Windows Event Log is only readable at the
/// machine, and an in-memory "last error" is lost on restart.
/// </summary>
public sealed partial class FileLogStore : IDisposable
{
    public const string CurrentFileName = "service.log";
    private const string LegacyCrashFileName = "last-crash.txt";
    private const string LegacyRunMarkerFileName = "running.marker";

    private readonly object _sync = new();
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;
    private readonly UTF8Encoding _encoding = new(encoderShouldEmitUTF8Identifier: false);
    private string? _secret;
    private string? _runMarkerPath;
    private StreamWriter? _writer;
    private bool _disposed;

    private FileLogStore() { }

    public FileLogStore(string directory, long maxFileBytes = 1024 * 1024, int retainedFiles = 2)
    {
        Folder = Path.GetFullPath(directory);
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _retainedFiles = Math.Max(1, retainedFiles);
        System.IO.Directory.CreateDirectory(Folder);
    }

    public static FileLogStore CreateForService(ISharedPathProvider paths)
    {
        try
        {
            return new FileLogStore(paths.GetSharedTempDirectory("Logs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileLogStore();
        }
    }

    public string? Folder { get; }

    public bool IsAvailable => Folder is not null;

    public string CurrentPath => InLogFolder(CurrentFileName);

    /// <summary>The exact bot token, so it is removed even if its shape is unusual.</summary>
    public void SetSecret(string? secret)
    {
        _secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string? secret = _secret;
        if (secret is not null)
        {
            text = text.Replace(secret, "[bot-token]", StringComparison.Ordinal);
        }

        return BotTokenPattern().Replace(text, "[bot-token]");
    }

    public void Write(string level, string category, string message, Exception? exception = null)
    {
        if (!IsAvailable)
        {
            return;
        }

        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [").Append(level).Append("] ")
            .Append(category).Append(": ")
            .Append(message);

        if (exception is not null)
        {
            entry.AppendLine().Append("    ").Append(exception.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        }

        string line = Redact(entry.ToString());

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                RotateIfNeeded();
                _writer ??= OpenWriter();
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the service down.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>Most recent lines of the current file, newest last, bounded for a chat message.</summary>
    public string ReadTail(int maxLines, int maxCharacters)
    {
        const int WindowBytes = 128 * 1024;
        if (!IsAvailable)
        {
            return string.Empty;
        }

        string text;
        lock (_sync)
        {
            _writer?.Flush();
            text = ReadEnd(CurrentPath, WindowBytes);

            // Right after a rotation the current file holds only a few lines, so the
            // tail continues into the previous file.
            if (text.Length < WindowBytes / 2)
            {
                text = ReadEnd(RotatedPath(1), WindowBytes / 2) + text;
            }
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var selected = new List<string>();
        int characters = 0;

        for (int i = lines.Length - 1; i >= 0 && selected.Count < maxLines; i--)
        {
            string line = lines[i].TrimEnd('\r');
            if (characters + line.Length + 1 > maxCharacters)
            {
                if (selected.Count == 0)
                {
                    selected.Add(line[..Math.Min(line.Length, maxCharacters)]);
                }

                break;
            }

            selected.Add(line);
            characters += line.Length + 1;
        }

        selected.Reverse();
        return string.Join('\n', selected);
    }

    /// <summary>All retained log files as one stream, oldest first.</summary>
    public MemoryStream CreateSnapshot()
    {
        var snapshot = new MemoryStream();
        if (!IsAvailable)
        {
            return snapshot;
        }

        lock (_sync)
        {
            _writer?.Flush();
            for (int index = _retainedFiles; index >= 0; index--)
            {
                string path = index == 0 ? CurrentPath : RotatedPath(index);
                if (!File.Exists(path))
                {
                    continue;
                }

                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.CopyTo(snapshot);
            }
        }

        snapshot.Position = 0;
        return snapshot;
    }

    /// <summary>Creates a marker for this run without overwriting unreported runs.</summary>
    public void BeginRun()
    {
        lock (_sync)
        {
            if (!IsAvailable || _runMarkerPath is not null)
            {
                return;
            }

            try
            {
                string markerPath = InLogFolder($"running-{Guid.NewGuid():N}.marker");
                File.WriteAllText(markerPath, DateTimeOffset.Now.ToString("o") + Environment.NewLine, _encoding);
                _runMarkerPath = markerPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write("WRN", nameof(FileLogStore), $"Run marker unavailable: {ex.Message}");
            }
        }
    }

    public void EndRunCleanly()
    {
        lock (_sync)
        {
            if (_runMarkerPath is not { } markerPath)
            {
                return;
            }

            _runMarkerPath = null;
            try
            {
                File.Delete(markerPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Write("WRN", nameof(FileLogStore), $"Could not clear the current run marker: {ex.Message}");
            }
        }
    }

    /// <summary>Recorded synchronously because the process may be gone a moment later.</summary>
    public void RecordCrash(string source, Exception? exception)
    {
        Write("CRT", source, "Fatal error; the service is going down.", exception);

        lock (_sync)
        {
            if (_runMarkerPath is not { } markerPath)
            {
                return;
            }

            try
            {
                string summary = exception is null
                    ? source
                    : $"{source}: {exception.GetType().Name}: {exception.Message}";
                File.AppendAllText(markerPath, Redact(summary) + Environment.NewLine, _encoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal IReadOnlyList<UncleanRun> ReadPreviousRuns()
    {
        if (Folder is not { } folder)
        {
            return [];
        }

        string legacyMarker = InLogFolder(LegacyRunMarkerFileName);
        var pending = new List<UncleanRun>();
        foreach (string path in Directory.EnumerateFiles(folder, "running*.marker"))
        {
            if (path.Equals(_runMarkerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(path, _encoding);
            DateTimeOffset started = DateTimeOffset.TryParse(reader.ReadLine(), out DateTimeOffset time)
                ? time
                : File.GetLastWriteTime(path);
            bool legacy = path.Equals(legacyMarker, StringComparison.OrdinalIgnoreCase);
            string? legacyCrash = legacy ? InLogFolder(LegacyCrashFileName) : null;
            string cause = legacyCrash is null ? reader.ReadToEnd().Trim() : ReadLegacyCrash(legacyCrash);
            string id = legacy ? started.ToUnixTimeMilliseconds().ToString() : Path.GetFileNameWithoutExtension(path);
            pending.Add(new UncleanRun(path, started, Redact(cause), id, legacyCrash));
        }

        return pending.OrderBy(run => run.Started).ToArray();
    }

    private string ReadLegacyCrash(string path)
    {
        try
        {
            return File.ReadAllText(path, _encoding).Trim();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    internal sealed record UncleanRun(string Path, DateTimeOffset Started, string Cause, string Id, string? LegacyCrash)
    {
        public void Acknowledge()
        {
            if (LegacyCrash is not null)
            {
                File.Delete(LegacyCrash);
            }

            File.Delete(Path);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private string ReadEnd(string path, int maxBytes)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        byte[] buffer = new byte[stream.Length - start];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        string text = _encoding.GetString(buffer, 0, read);

        if (start > 0)
        {
            // The window probably starts mid-line; drop the partial first line.
            int firstBreak = text.IndexOf('\n');
            text = firstBreak >= 0 ? text[(firstBreak + 1)..] : string.Empty;
        }

        return text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
    }

    private StreamWriter OpenWriter()
    {
        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, _encoding);
    }

    private void RotateIfNeeded()
    {
        var current = new FileInfo(CurrentPath);
        if (!current.Exists || current.Length < _maxFileBytes)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;

        for (int index = _retainedFiles; index >= 1; index--)
        {
            string source = index == 1 ? CurrentPath : RotatedPath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, RotatedPath(index), overwrite: true);
            }
        }
    }

    private string InLogFolder(string name) => Path.Combine(
        Folder ?? throw new InvalidOperationException("Private log storage is unavailable."), name);

    private string RotatedPath(int index) => InLogFolder($"service.{index}.log");

    [GeneratedRegex(@"\d{6,}:[A-Za-z0-9_-]{30,}")]
    private static partial Regex BotTokenPattern();
}
