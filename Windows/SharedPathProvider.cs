namespace WinSystemHelper;

public sealed class SharedPathProvider : ISharedPathProvider
{
    public string GetSharedTempDirectory(string purpose)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            ServiceConstants.ServiceName,
            purpose);

        Directory.CreateDirectory(directory);
        return directory;
    }
}
