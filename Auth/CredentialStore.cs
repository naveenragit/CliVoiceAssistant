using Windows.Security.Credentials;

namespace VoiceAssistant.Auth;

/// <summary>
/// Stores and retrieves secrets using the Windows Credential Manager
/// (Windows.Security.Credentials.PasswordVault — visible in
/// Control Panel → Credential Manager → Windows Credentials).
///
/// Secrets never touch disk in plaintext; they are protected by the
/// OS user-session key (same protection as stored browser passwords).
///
/// Layout in Credential Manager:
///   Resource : "VoiceAssistant/<purpose>"
///   UserName : fixed sentinel "voiceassistant"
///   Password : the secret value
///
/// Purposes used by this app:
///   ApiKey   → Azure OpenAI API key (legacy / direct-key mode)
/// </summary>
public static class CredentialStore
{
    private const string AppPrefix  = "VoiceAssistant";
    private const string UserSentinel = "voiceassistant";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Stores (or updates) an API key in the Windows Credential Manager.</summary>
    public static void StoreApiKey(string apiKey)
        => Store("ApiKey", apiKey);

    /// <summary>
    /// Returns the stored API key, or <c>null</c> if none has been saved.
    /// </summary>
    public static string? RetrieveApiKey()
        => Retrieve("ApiKey");

    /// <summary>Removes the API key from the Windows Credential Manager.</summary>
    public static void DeleteApiKey()
        => Delete("ApiKey");

    /// <summary>True if an API key exists in the vault.</summary>
    public static bool HasApiKey()
        => RetrieveApiKey() != null;

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string ResourceName(string purpose) => $"{AppPrefix}/{purpose}";

    private static void Store(string purpose, string secret)
    {
        var vault    = new PasswordVault();
        var resource = ResourceName(purpose);

        // Remove any existing entry first (PasswordVault throws on duplicate Add).
        try { vault.Remove(vault.Retrieve(resource, UserSentinel)); } catch { /* not found */ }

        vault.Add(new PasswordCredential(resource, UserSentinel, secret));
    }

    private static string? Retrieve(string purpose)
    {
        try
        {
            var vault = new PasswordVault();
            var cred  = vault.Retrieve(ResourceName(purpose), UserSentinel);
            cred.RetrievePassword();   // must call before reading Password
            return cred.Password;
        }
        catch { return null; }
    }

    private static void Delete(string purpose)
    {
        try
        {
            var vault = new PasswordVault();
            var cred  = vault.Retrieve(ResourceName(purpose), UserSentinel);
            vault.Remove(cred);
        }
        catch { /* not found — silently ignore */ }
    }
}
