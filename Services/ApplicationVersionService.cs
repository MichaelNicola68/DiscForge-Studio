using System.Reflection;

namespace DiscForgeStudio.Services;

public static class ApplicationVersionService
{
    public const string ProductName = "DiscForge Studio";
    public const string FallbackVersion = "1.0.0-rc.1";

    public static string Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            var informational = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Split('+', 2)[0];

            return assembly?.GetName().Version?.ToString(3) ?? FallbackVersion;
        }
    }

    public static string ProductLabel => $"{ProductName} {Current}";
    public static string WindowTitle => ProductLabel;
    public static string VersionLabel => $"Version {Current}";
    public static string ReportTitle(string reportName) => $"{ProductLabel} - {reportName}";
}
