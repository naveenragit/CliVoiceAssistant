using Azure.Core;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Auth;

/// <summary>
/// Monitors Azure AD token expiry and proactively refreshes before it expires.
/// When the token is about to expire, fires <see cref="TokenRefreshed"/> so the
/// caller can reconnect the WebSocket with the new token.
///
/// Usage:
///   var refresh = new TokenRefreshService(tokenProvider);
///   refresh.TokenRefreshed += async (_, token) => { /* reconnect WS */ };
///   refresh.TokenExpiring  += (_, mins) => { /* update UI */ };
///   refresh.Start();
///   ...
///   refresh.Stop();
/// </summary>
public sealed class TokenRefreshService : IDisposable
{
    private readonly TokenProvider _tokens;
    private System.Threading.Timer?  _timer;
    private DateTimeOffset        _expiresOn;
    private bool                  _refreshing;

    /// <summary>Fires when a fresh token has been acquired. Arg = new bearer token string.</summary>
    public event EventHandler<string>? TokenRefreshed;

    /// <summary>Fires periodically with minutes remaining until expiry (for UI display).</summary>
    public event EventHandler<int>? TokenExpiring;

    /// <summary>Fires when token refresh fails (e.g. needs interactive login).</summary>
    public event EventHandler<string>? TokenRefreshFailed;

    /// <summary>How many minutes before expiry to trigger a proactive refresh.</summary>
    public int RefreshBufferMinutes { get; set; } = 5;

    /// <summary>How often to check token status (seconds).</summary>
    public int CheckIntervalSeconds { get; set; } = 30;

    public DateTimeOffset ExpiresOn => _expiresOn;
    public int MinutesRemaining => Math.Max(0, (int)(_expiresOn - DateTimeOffset.UtcNow).TotalMinutes);
    public bool IsExpired => _expiresOn <= DateTimeOffset.UtcNow;
    public bool IsExpiringSoon => _expiresOn <= DateTimeOffset.UtcNow.AddMinutes(RefreshBufferMinutes);

    public TokenRefreshService(TokenProvider tokens)
    {
        _tokens = tokens;
    }

    /// <summary>Start periodic token monitoring. Call after initial authentication.</summary>
    public void Start(DateTimeOffset initialExpiresOn)
    {
        _expiresOn = initialExpiresOn;
        AppLog.Info($"TokenRefresh: monitoring started — expires at {_expiresOn:HH:mm:ss} " +
                    $"({MinutesRemaining} min remaining)");

        _timer = new System.Threading.Timer(OnTick, null,
            TimeSpan.FromSeconds(CheckIntervalSeconds),
            TimeSpan.FromSeconds(CheckIntervalSeconds));
    }

    /// <summary>Update the known expiry after a successful connection/reconnection.</summary>
    public void UpdateExpiry(DateTimeOffset expiresOn)
    {
        _expiresOn = expiresOn;
        AppLog.Info($"TokenRefresh: expiry updated — {MinutesRemaining} min remaining");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async void OnTick(object? state)
    {
        if (_refreshing) return;

        var remaining = MinutesRemaining;

        // Notify UI of remaining time when getting close
        if (remaining <= 10)
            TokenExpiring?.Invoke(this, remaining);

        // Proactive refresh when within the buffer window
        if (IsExpiringSoon && !_refreshing)
        {
            _refreshing = true;
            AppLog.Info($"TokenRefresh: token expiring in {remaining} min — refreshing proactively");

            try
            {
                var token = await _tokens.GetTokenAsync();
                _expiresOn = GetExpiryFromToken();
                AppLog.Info($"TokenRefresh: refreshed — new expiry in {MinutesRemaining} min");
                TokenRefreshed?.Invoke(this, token);
            }
            catch (Exception ex)
            {
                AppLog.Error($"TokenRefresh: refresh failed — {ex.Message}");
                TokenRefreshFailed?.Invoke(this, ex.Message);
            }
            finally
            {
                _refreshing = false;
            }
        }
    }

    /// <summary>
    /// Force an immediate token refresh (e.g. after a WebSocket auth error).
    /// Returns the new token or throws.
    /// </summary>
    public async Task<string> ForceRefreshAsync(CancellationToken ct = default)
    {
        AppLog.Info("TokenRefresh: forced refresh requested");
        _tokens.ClearCache();
        var token = await _tokens.GetTokenAsync(ct);
        _expiresOn = GetExpiryFromToken();
        AppLog.Info($"TokenRefresh: forced refresh succeeded — {MinutesRemaining} min remaining");
        TokenRefreshed?.Invoke(this, token);
        return token;
    }

    private DateTimeOffset GetExpiryFromToken()
    {
        return _tokens.ExpiresOn;
    }

    public void Dispose() => Stop();
}
