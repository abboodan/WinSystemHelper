using System.Collections.Concurrent;
using System.Text;

namespace WinSystemHelper;

internal static partial class PowerShellScripts
{
    private const string ResourcePrefix = "WinSystemHelper.Scripts.";
    private static readonly ConcurrentDictionary<string, string> Templates = new(StringComparer.Ordinal);

    public static string GetDevelopmentFormSetup() => Load("DevelopmentFormSetup");

    public static string BuildLocalConsentScript(
        string directory, string formSetup, string title, string prompt, string allow, string stopLabel, string active)
    {
        return Render("LocalConsent",
            ("directory", directory),
            ("formSetup", formSetup),
            ("title", title),
            ("prompt", prompt),
            ("allow", allow),
            ("stopLabel", stopLabel),
            ("active", active));
    }

    public static string BuildInlineCommandArguments(string name)
    {
        return $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{Load(name)}\"";
    }

    private static string Render(string name, params (string Name, string Value)[] parameters)
    {
        string script = Load(name);
        foreach ((string parameter, string value) in parameters)
        {
            string base64Token = "{{base64:" + parameter + "}}";
            script = script.Contains(base64Token, StringComparison.Ordinal)
                ? script.Replace(base64Token, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), StringComparison.Ordinal)
                : script.Replace("{{" + parameter + "}}", value, StringComparison.Ordinal);
        }

        return script;
    }

    private static string Load(string name)
    {
        return Templates.GetOrAdd(name, static template =>
        {
            string resourceName = ResourcePrefix + template + ".ps1";
            using Stream stream = typeof(PowerShellScripts).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"PowerShell resource '{resourceName}' was not found.");
            using StreamReader reader = new(stream, Encoding.UTF8);
            string script = reader.ReadToEnd().TrimEnd('\r', '\n');
            return script.Contains("{{copyTree}}", StringComparison.Ordinal)
                ? script.Replace("{{copyTree}}", Load("CopyTree"), StringComparison.Ordinal)
                : script;
        });
    }
}
