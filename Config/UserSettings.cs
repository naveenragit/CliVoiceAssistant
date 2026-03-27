using System.Text.Json;
using VoiceAssistant.Auth;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Config;

/// <summary>
/// User-facing settings saved to %APPDATA%\VoiceAssistant\settings.json.
/// Only non-sensitive values are stored here.
///
/// Sensitive secrets (API keys) are stored exclusively in the
/// Windows Credential Manager via <see cref="CredentialStore"/>.
/// Azure AD tokens are cached in a DPAPI-encrypted file by Azure.Identity.
/// </summary>
public sealed class UserSettings
{
    /// <summary>Normalised HTTPS endpoint, e.g. https://myresource.openai.azure.com</summary>
    public string Endpoint       { get; set; } = "";

    /// <summary>Deployment / model name, e.g. gpt-4o-realtime-preview</summary>
    public string DeploymentName { get; set; } = "gpt-4o-realtime-preview";

    /// <summary>"aad" = Azure AD interactive login | "apikey" = static API key from vault</summary>
    public string AuthMode       { get; set; } = "aad";

    /// <summary>True once the user has completed the setup dialog.</summary>
    public bool   IsConfigured   { get; set; } = false;

    // ── Persistence ──────────────────────────────────────────────────────────

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "VoiceAssistant");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static UserSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new UserSettings();
        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
        }
        catch { return new UserSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static void Delete()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        // Also purge any stored secrets from the OS vault.
        CredentialStore.DeleteApiKey();
    }

    // ── Endpoint normalisation ───────────────────────────────────────────────
    // Accepts any of these common paste formats from the Foundry / Azure portal:
    //   https://myresource.openai.azure.com
    //   myresource.openai.azure.com
    //   eastus.api.azureml.ms;sub;rg;project        ← Foundry connection string
    //   https://eastus.api.azureml.ms;sub;rg;project

    public static string NormalizeEndpoint(string raw)
    {
        raw = (raw ?? "").Trim().TrimEnd('/');

        // Strip trailing connection-string segments (;sub;rg;project)
        var semicolonIdx = raw.IndexOf(';');
        if (semicolonIdx >= 0)
            raw = raw[..semicolonIdx];

        // Ensure https:// scheme
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;

        return raw;
    }

    public static bool IsValidEndpoint(string endpoint)
    {
        var normalised = NormalizeEndpoint(endpoint);
        return Uri.TryCreate(normalised, UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            && uri.Host.Length > 4;
    }

    /// <summary>
    /// Validates settings integrity. If endpoint or deployment name are invalid,
    /// resets <see cref="IsConfigured"/> to false so the setup dialog re-appears.
    /// </summary>
    public void Validate()
    {
        if (!IsConfigured) return;

        if (string.IsNullOrWhiteSpace(Endpoint) ||
            !IsValidEndpoint(Endpoint) ||
            string.IsNullOrWhiteSpace(DeploymentName))
        {
            AppLog.Warn("UserSettings.Validate: invalid endpoint or deployment — resetting IsConfigured.");
            IsConfigured = false;
        }
    }
}
