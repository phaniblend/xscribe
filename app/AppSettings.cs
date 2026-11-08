using System.IO;
using System.Text.Json;

namespace XScribeDesktop;

public static class AppSettings
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XScribeDesktop",
        "settings.json");

    public static string? AnthropicApiKey
    {
        get
        {
            try
            {
                if (!File.Exists(Path)) return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                var json = File.ReadAllText(Path);
                var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("anthropicApiKey", out var k) ? k.GetString()
                    : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            }
            catch { return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"); }
        }
        set
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(new { anthropicApiKey = value }));
        }
    }
}
