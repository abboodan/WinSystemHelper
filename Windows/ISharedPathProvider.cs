namespace WinSystemHelper;

public interface ISharedPathProvider
{
    string GetSharedTempDirectory(string purpose);
}
