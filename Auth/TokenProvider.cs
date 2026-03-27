using Azure.Core;
using Azure.Identity;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Auth;

/// <summary>
/// Acquires Azure AD access tokens for Azure OpenAI / Cognitive Services.
///
/// Strategy (in order):
///   1. In-memory cached token (if still valid)
///   2. AzureCliCredential — instant if user is logged into 'az cli'
///   3. InteractiveBrowserCredential with persisted MSAL cache — silent
///      refresh or browser login
///
/// Same MSAL cache name ("VoiceAssistant") as v2 for token reuse.
/// Scope: https://cognitiveservices.azure.com/.default
/// </summary>
public sealed class TokenProvider
{
    private static readonly string[] Scopes =
        ["https://cognitiveservices.azure.com/.default"];

    private readonly AzureCliCredential              _azureCli;
    private readonly InteractiveBrowserCredential    _interactive;
    private AccessToken                               _cached;

    public bool IsAuthenticated => _cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5);

    /// <summary>The expiry time of the current cached token.</summary>
    public DateTimeOffset ExpiresOn => _cached.ExpiresOn;

    public TokenProvider()
    {
        _azureCli = new AzureCliCredential();

        var options = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "VoiceAssistant",
                UnsafeAllowUnencryptedStorage = false,
            },
            TenantId = "common",
        };
        _interactive = new InteractiveBrowserCredential(options);

        AppLog.Info("TokenProvider: initialized (AzureCli → InteractiveBrowser)");
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a valid bearer token. Tries az cli first (no browser needed),
    /// then falls back to interactive browser sign-in.
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated)
        {
            AppLog.Info("TokenProvider: using cached token (still valid)");
            return _cached.Token;
        }

        // Try Azure CLI first — instant, no browser
        try
        {
            AppLog.Info("TokenProvider: trying AzureCliCredential...");
            _cached = await _azureCli.GetTokenAsync(new TokenRequestContext(Scopes), ct);
            AppLog.Info($"TokenProvider: AzureCli token acquired (expires {_cached.ExpiresOn:HH:mm:ss})");
            return _cached.Token;
        }
        catch (Exception ex)
        {
            AppLog.Info($"TokenProvider: AzureCli unavailable ({ex.GetType().Name}), trying InteractiveBrowser...");
        }

        // Fall back to interactive browser (uses persisted MSAL cache)
        _cached = await _interactive.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        AppLog.Info($"TokenProvider: InteractiveBrowser token acquired (expires {_cached.ExpiresOn:HH:mm:ss})");
        return _cached.Token;
    }

    /// <summary>
    /// Called from SetupOverlay to perform the initial interactive sign-in.
    /// </summary>
    public async Task<bool> AuthenticateInteractiveAsync(
        Action<string> progress,
        CancellationToken ct = default)
    {
        try
        {
            // Try az cli first (no browser popup)
            progress("Checking Azure CLI login…");
            try
            {
                _cached = await _azureCli.GetTokenAsync(new TokenRequestContext(Scopes), ct);
                AppLog.Info($"TokenProvider: setup auth via AzureCli (expires {_cached.ExpiresOn:HH:mm:ss})");
                progress("Signed in via Azure CLI.");
                return true;
            }
            catch
            {
                AppLog.Info("TokenProvider: AzureCli not available for setup, using browser...");
            }

            progress("Opening browser for Microsoft sign-in…");
            _cached = await _interactive.GetTokenAsync(new TokenRequestContext(Scopes), ct);
            progress("Signed in successfully.");
            return true;
        }
        catch (OperationCanceledException)
        {
            progress("Sign-in cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            progress($"Sign-in failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the cached token so the next call triggers a fresh login.
    /// </summary>
    public void ClearCache() => _cached = default;
}
