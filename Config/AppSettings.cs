using System.Text.Json;

namespace VoiceAssistant.Config;

// ── Settings model ──────────────────────────────────────────────────────────

public class AppSettings
{
    public string        Mode   { get; set; } = "proxy";   // "proxy" | "azure"
    public ProxySettings Proxy   { get; set; } = new();
    public AzureSettings Azure   { get; set; } = new();
    public VoiceSettings Voice   { get; set; } = new();
    public CopilotSettings Copilot { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
    }
}

public class ProxySettings
{
    public string Url { get; set; } = "ws://localhost:3000";
}

public class AzureSettings
{
    public string Endpoint   { get; set; } = "";
    public string ApiKey     { get; set; } = "";
    public string Deployment { get; set; } = "gpt-4o-realtime-preview";
    public string ApiVersion { get; set; } = "2024-10-01-preview";
}

public class VoiceSettings
{
    public string Name              { get; set; } = "alloy";
    public double VadThreshold      { get; set; } = 0.5;
    public int    SilenceDurationMs { get; set; } = 700;
    public string Instructions      { get; set; } = "You are a helpful voice assistant. Be concise.";
}

public class CopilotSettings
{
    /// <summary>
    /// Text appended to every message injected into the Copilot CLI input box.
    /// Leave empty to disable. Use "\n" in the JSON value for a newline prefix.
    /// Example: " respond in bullet points"
    /// </summary>
    public string InstructionSuffix { get; set; } = "";

    /// <summary>
    /// When true (default), always press Enter after injecting text — submits the command immediately.
    /// Set false to leave text in the input box for the user to review before submitting.
    /// </summary>
    public bool AutoSubmit { get; set; } = true;
}
