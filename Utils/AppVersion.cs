namespace CSharpScraper.Utils;

public static class AppVersion
{
    public static string Value => typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
