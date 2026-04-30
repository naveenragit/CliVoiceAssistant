using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAssistant.Audio;
using VoiceAssistant.Auth;
using VoiceAssistant.Config;
using VoiceAssistant.Infrastructure;
using VoiceAssistant.Tools;

namespace VoiceAssistant.Networking;

// ── Event args ──────────────────────────────────────────────────────────────

public class TranscriptEventArgs(string role, string text, bool isDelta = false) : EventArgs
{
    public string Role    { get; } = role;
    public string Text    { get; } = text;
    public bool   IsDelta { get; } = isDelta;
}

public class StatusEventArgs(string message, ClientState state) : EventArgs
{
    public string      Message { get; } = message;
    public ClientState State   { get; } = state;
}

public enum ClientState { Idle, Connecting, Listening, Thinking, Speaking, Error }

// ── Client ──────────────────────────────────────────────────────────────────

public sealed class RealtimeClient : IAsyncDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const int BytesPerMs         = 48; // 24000 * 2 / 1000
    private const int WebSocketBufferSize = 65536;
    private const int ConnectTimeoutSeconds = 60;
    private const int WebSocketKeepAliveSeconds = 20;
    private const int SpeakTextWaitIntervalMs = 500;
    private const int SpeakTextMaxWaitMs = 15000;

    // Reconnection backoff: 1s → 2s → 4s → 8s → 16s → 30s (cap)
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectBaseDelayMs = 1000;
    private const int ReconnectMaxDelayMs  = 30000;

    // Public events – fired on the ThreadPool; callers must marshal to UI thread.
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>?     StatusChanged;
    public event EventHandler<float>?               VolumeChanged;   // 0–1 RMS

    private readonly AppSettings          _cfg;
    private readonly TokenProvider?       _tokens;   // non-null when using Azure AD auth
    private readonly UserSettings?        _settings; // first-run user settings (overrides appsettings)
    private readonly ToolRegistry         _tools;
    private ClientWebSocket?              _webSocket;
    private CancellationTokenSource      _cancellationTokenSource   = new();

    // Audio — delegated to AudioManager
    private AudioManager?                _audio;

    private volatile bool _isConnected;
    private volatile bool _isModelSpeaking;    // suppress mic echo during model output
    private volatile bool _isPttActive;        // true only while PTT button/key is held
    private volatile bool _isAlwaysOn;         // true = server VAD; false = manual PTT
    private volatile bool _isResponseInProgress; // true between response.created and response.done
    private volatile bool _isUserDisconnected;   // true when user explicitly disconnects (suppresses reconnect)
    private volatile bool _lastResponseFailed;   // true when response.done status=failed (for SpeakText retry)
    private volatile bool _responseHadAudio;     // true if the current response sent any audio delta

    // Serializes all WebSocket sends — ClientWebSocket does not support concurrent sends.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Tracks bytes of audio sent during the current PTT press so we can skip
    // commit if the user tapped too briefly (server requires ≥100ms).
    // 100ms @ 24kHz 16-bit mono = 4800 bytes
    private int _pttAudioBytes;
    private const int MinPttAudioBytes = 4800; // 100ms minimum

    // ── Per-turn latency tracking ─────────────────────────────────────────────
    // Stopwatch resets when PTT is released. Each stage logs elapsed ms so it's
    // easy to see exactly where time is spent: STT / server thinking / first audio.
    private readonly System.Diagnostics.Stopwatch _turnTimer = new();
    private bool _firstAudioDelta;
    private bool _firstTranscriptDelta;

    // Accumulates streamed function-call arguments, keyed by call_id
    private readonly ConcurrentDictionary<string, (string Name, StringBuilder Args)>
        _pendingCalls = new();

    // ── Public PTT API ────────────────────────────────────────────────────────

    /// <summary>Called when the user presses and holds the mic button / spacebar.</summary>
    public void StartPtt()
    {
        if (!_isConnected) return;
        if (_isAlwaysOn) return;   // PTT is a no-op in always-on mode

        // Barge-in: if the model is speaking, interrupt it immediately
        if (_isModelSpeaking || _isResponseInProgress)
        {
            AppLog.Info("PTT barge-in: interrupting model playback");
            _ = InterruptPlaybackAsync();
        }

        _isPttActive     = true;
        Interlocked.Exchange(ref _pttAudioBytes, 0);
        AppLog.Info("PTT start");
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
    }

    /// <summary>Called when the user releases the mic button / spacebar.</summary>
    public void StopPtt()
    {
        if (!_isPttActive) return;
        _isPttActive = false;

        var capturedBytes = Volatile.Read(ref _pttAudioBytes);

        // Too short — server requires ≥100ms. Just abort silently.
        if (capturedBytes < MinPttAudioBytes)
        {
            var ms = capturedBytes / BytesPerMs;
            AppLog.Info($"PTT too short ({ms}ms, {capturedBytes} bytes) — skipping commit");
            StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
            return;
        }

        // Reset turn timer — T=0 is when the user finishes speaking
        _turnTimer.Restart();
        _firstAudioDelta      = false;
        _firstTranscriptDelta = false;
        AppLog.Info($"[TIMING] T=0 PTT released — {capturedBytes / BytesPerMs}ms audio captured, committing");

        StatusChanged?.Invoke(this, new StatusEventArgs("Processing…", ClientState.Thinking));

        _ = Task.Run(async () =>
        {
            try
            {
                await SendJsonAsync(new { type = "input_audio_buffer.commit" });
                AppLog.Info("[TIMING] +0ms  input_audio_buffer.commit sent");

                // Only request a new response if the model isn't already responding
                if (!_isResponseInProgress)
                {
                    await SendJsonAsync(new { type = "response.create" });
                    AppLog.Info("[TIMING] +0ms  response.create sent");
                }
                else
                {
                    AppLog.Info("Skipped response.create — response already in progress");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"StopPtt commit failed: {ex.Message}");
            }
        });
    }

    // ── Constructors ──────────────────────────────────────────────────────────

    // Original: uses appsettings.json (api-key or proxy mode)
    public RealtimeClient(AppSettings cfg, ToolRegistry tools)
    {
        _cfg      = cfg;
        _tools    = tools;
        _tokens   = null;
        _settings = null;
    }

    // First-run: uses UserSettings (endpoint + deployment) with Azure AD token
    public RealtimeClient(AppSettings cfg, UserSettings settings, TokenProvider tokens, ToolRegistry tools)
    {
        _cfg      = cfg;
        _settings = settings;
        _tokens   = tokens;
        _tools    = tools;
    }

    // ── Connect ─────────────────────────────────────────────────────────────

    public async Task ConnectAsync()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _isUserDisconnected = false;

        // Hard timeout: 60s to allow time for interactive browser sign-in
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, timeout.Token);

        _webSocket = new ClientWebSocket();
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(WebSocketKeepAliveSeconds);

        AppLog.Info("ConnectAsync: building connection URI...");
        Uri uri;
        try
        {
            uri = await BuildUriAsync(linked.Token);
            AppLog.Info($"ConnectAsync: URI built successfully: {uri.Host}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            AppLog.Error("ConnectAsync: token acquisition timed out after " + ConnectTimeoutSeconds + "s");
            throw new TimeoutException("Token acquisition timed out after " + ConnectTimeoutSeconds + " s. " +
                "Check your Azure AD sign-in or network connectivity.");
        }

        AppLog.Info($"Connecting to {uri.Host}…");
        StatusChanged?.Invoke(this, new StatusEventArgs("Connecting…", ClientState.Connecting));

        try
        {
            await _webSocket.ConnectAsync(uri, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"WebSocket connection to {uri.Host} timed out after 20 s. " +
                "Verify the endpoint URL and your network.");
        }

        AppLog.Info("WebSocket connected.");
        _isConnected = true;

        // Send session config when not going through the proxy.
        if (_settings != null || _cfg.Mode == "azure")
            await SendSessionUpdateAsync();

        InitializeAudioCaptureAndPlayback();
        _ = Task.Run(ReceiveLoopAsync, _cancellationTokenSource.Token);
        AppLog.Info("Listening.");
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
    }

    private async Task<Uri> BuildUriAsync(CancellationToken ct = default)
    {
        // ── Azure AD path (first-run / UserSettings) ───────────────────────
        if (_tokens != null && _settings != null)
        {
            var endpoint  = _settings.Endpoint.TrimEnd('/').Replace("https://", "wss://");
            var deploymentName = Uri.EscapeDataString(_settings.DeploymentName);
            var url = $"{endpoint}/openai/realtime?api-version=2025-04-01-preview&deployment={deploymentName}";

            AppLog.Info($"Endpoint: {endpoint}  Deployment: {deploymentName}  AuthMode: {_settings.AuthMode}");

            if (_settings.AuthMode == "apikey")
            {
                var key = CredentialStore.RetrieveApiKey()
                    ?? throw new InvalidOperationException(
                        "API key not found in Windows Credential Manager. " +
                        "Re-open Settings (⚙) to re-enter your key.");
                _webSocket!.Options.SetRequestHeader("api-key", key);
                AppLog.Info("Using API key from Credential Manager.");
            }
            else
            {
                AppLog.Info("Fetching Azure AD bearer token…");
                var token = await _tokens.GetTokenAsync(ct);
                _webSocket!.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                AppLog.Info("Bearer token acquired.");
            }
            return new Uri(url);
        }

        // ── Legacy api-key path (appsettings.json azure mode) ─────────────
        if (_cfg.Mode == "azure")
        {
            var endpoint  = _cfg.Azure.Endpoint.TrimEnd('/').Replace("https://", "wss://");
            var url = $"{endpoint}/openai/realtime?api-version={_cfg.Azure.ApiVersion}&deployment={_cfg.Azure.Deployment}";
            AppLog.Info($"Legacy azure mode — endpoint: {endpoint}");
            _webSocket!.Options.SetRequestHeader("api-key", _cfg.Azure.ApiKey);
            return new Uri(url);
        }

        // ── Proxy mode ─────────────────────────────────────────────────────
        AppLog.Info($"Proxy mode — {_cfg.Proxy.Url}");
        return new Uri(_cfg.Proxy.Url);
    }

    // ── Session config (Azure-direct mode only) ──────────────────────────────

    private Task SendSessionUpdateAsync() => SendJsonAsync(new
    {
        type    = "session.update",
        session = new
        {
            modalities               = new[] { "audio", "text" },
            voice                    = _cfg.Voice.Name,
            input_audio_format       = "pcm16",
            output_audio_format      = "pcm16",
            input_audio_transcription = new { model = "whisper-1" },
            // PTT mode disables server VAD so we can manually commit audio on release.
            // Always-on mode uses server VAD to auto-detect speech boundaries.
            turn_detection           = _isAlwaysOn
                ? (object)new
                {
                    type                = "server_vad",
                    threshold           = _cfg.Voice.VadThreshold,
                    prefix_padding_ms   = 300,
                    silence_duration_ms = _cfg.Voice.SilenceDurationMs,
                    create_response     = true,
                }
                : null,
            instructions = _cfg.Voice.Instructions +
                UserMemory.ToSystemPromptBlock() +
                " ABSOLUTE RULE: Forward EVERY user request to Copilot CLI using send_to_copilot_cli." +
                " You are ONLY a voice relay — you do NOT answer questions, you do NOT ask for" +
                " clarification, you do NOT say 'I'm not sure' or 'could you explain'. You just" +
                " forward the user's words to Copilot CLI exactly as spoken." +
                " The ONLY exceptions where you do NOT forward to Copilot CLI:" +
                " 1) The user explicitly says 'remember that...' → use remember_fact tool" +
                " 2) The user says 'stop', 'cancel', 'never mind' → acknowledge and stop" +
                " 3) The user asks 'what do you know about me' → use recall_facts" +
                " 4) The user asks about their EMAIL (read emails, send email, reply to email," +
                "    check inbox, unread messages, email from someone) → use read_emails or send_email" +
                " 5) The user asks about TEAMS messages (read chats, Teams messages, reply on Teams," +
                "    send a Teams message, what did someone say on Teams) → use read_teams_chats or send_teams_message" +
                " For EVERYTHING else — questions, commands, vague requests, unclear asks —" +
                " forward to Copilot CLI immediately. Do not think, do not interpret, do not" +
                " ask follow-up questions. Just forward it." +
                " When Copilot CLI presents a selection menu (numbered options or arrow-key list)," +
                " and the user says which option to pick (e.g. 'select option 2', 'the first one'," +
                " 'yes', 'pick that'), use the select_option tool to navigate with arrow keys" +
                " and press enter. For option N, send 'down' with repeat=N-1, then send 'enter'." +
                " If the user just says 'yes' or confirms, send 'enter' to select the current option." +
                " When reading emails aloud, summarize: say sender, subject, and a brief preview." +
                " When reading Teams chats, say who sent what and when." +
                " Before sending any email or Teams message, confirm with the user first." +
                " When Copilot CLI responds, read the key information aloud concisely." +
                " Always use submit=true.",
            temperature  = 0.6,
            tools        = _tools.Definitions,
            tool_choice  = "auto",
        },
    });

    // ── Audio I/O (delegated to AudioManager) ─────────────────────────────────

    private void InitializeAudioCaptureAndPlayback()
    {
        _audio = new AudioManager();
        _audio.AudioCaptured += OnMicData;
        _audio.VolumeChanged += (_, rms) => VolumeChanged?.Invoke(this, rms);
        _audio.Initialize();
    }

    private async void OnMicData(object? sender, AudioCapturedEventArgs e)
    {
        // Transmit while PTT is held (PTT mode) or always (always-on mode).
        // Echo guard: suppress mic while model is playing back its own audio.
        if (!_isConnected || _isModelSpeaking) return;
        if (!_isPttActive && !_isAlwaysOn) return;
        if (_webSocket?.State != WebSocketState.Open) return;

        // Count bytes so StopPtt can verify minimum audio length
        Interlocked.Add(ref _pttAudioBytes, e.BytesRecorded);

        try
        {
            await SendJsonAsync(new
            {
                type  = "input_audio_buffer.append",
                audio = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded),
            }, skipIfBusy: true);
        }
        catch { /* ignore — connection may be closing */ }
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        var receiveBuffer  = new byte[WebSocketBufferSize];
        var messageBuilder = new StringBuilder();

        while (_webSocket?.State == WebSocketState.Open && !_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(receiveBuffer, _cancellationTokenSource.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                messageBuilder.Append(Encoding.UTF8.GetString(receiveBuffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                DispatchRealtimeServerEvent(messageBuilder.ToString());
                messageBuilder.Clear();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error($"ReceiveLoop error: {ex.GetType().Name}: {ex.Message}");
                UIMessageBus.PushSystem($"\u26a0 Connection lost: {ex.Message}", readAloud: true);
                break;
            }
        }

        _isConnected = false;

        // If the user explicitly disconnected, don't attempt auto-reconnect
        if (_isUserDisconnected)
        {
            SetStatus("Disconnected", ClientState.Idle);
            UIMessageBus.PushSystem("\ud83d\udd0c Disconnected.", readAloud: false);
            return;
        }

        // Attempt auto-reconnect with exponential backoff
        await AttemptReconnectAsync();
    }

    // ── Auto-reconnect ──────────────────────────────────────────────────────

    private async Task AttemptReconnectAsync()
    {
        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            if (_isUserDisconnected || _cancellationTokenSource.IsCancellationRequested)
                return;

            var delayMs = Math.Min(ReconnectBaseDelayMs * (1 << (attempt - 1)), ReconnectMaxDelayMs);
            AppLog.Info($"[RECONNECT] attempt {attempt}/{MaxReconnectAttempts} in {delayMs}ms");
            SetStatus($"Reconnecting ({attempt}/{MaxReconnectAttempts})…", ClientState.Connecting);
            UIMessageBus.PushSystem($"\ud83d\udd04 Reconnecting ({attempt}/{MaxReconnectAttempts})…", readAloud: false);

            try
            {
                await Task.Delay(delayMs);
                if (_isUserDisconnected) return;

                // Dispose old resources before reconnecting
                _audio?.Dispose();
                _audio = null;
                _webSocket?.Dispose();

                await ConnectAsync();
                AppLog.Info($"[RECONNECT] succeeded on attempt {attempt}");
                UIMessageBus.PushSystem("\u2705 Reconnected.", readAloud: false);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[RECONNECT] attempt {attempt} failed: {ex.Message}");
            }
        }

        // All retries exhausted
        AppLog.Error($"[RECONNECT] failed after {MaxReconnectAttempts} attempts");
        SetStatus("Disconnected", ClientState.Idle);
        UIMessageBus.PushSystem(
            $"\ud83d\udd0c Reconnection failed after {MaxReconnectAttempts} attempts. Click mic to reconnect.",
            readAloud: true);
    }

    // ── Event handler ─────────────────────────────────────────────────────────

    /// <summary>
    /// Interrupt model playback immediately (barge-in).
    /// Cancels the in-progress response, clears the audio buffer, and re-enables the mic.
    /// </summary>
    private async Task InterruptPlaybackAsync()
    {
        // Only send response.cancel if a response is actually in progress
        var wasActive = _isResponseInProgress;

        // Stop local playback immediately
        _isModelSpeaking     = false;
        _isResponseInProgress = false;
        _audio?.ClearPlaybackBuffer();

        // Tell the server to cancel the current response (only if one was active)
        if (wasActive)
        {
            try
            {
                await SendJsonAsync(new { type = "response.cancel" });
                AppLog.Info("Barge-in: response.cancel sent");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Barge-in: response.cancel failed: {ex.Message}");
            }
        }
        else
        {
            AppLog.Info("Barge-in: cleared local playback (no active server response to cancel)");
        }
    }

    private void DispatchRealtimeServerEvent(string json)
    {
        JsonNode? serverEvent;
        try { serverEvent = JsonNode.Parse(json); } catch { return; }
        if (serverEvent == null) return;

        switch (serverEvent["type"]?.GetValue<string>())
        {
            case "server.ready":               HandleServerReady(); break;
            case "input_speech_started":        HandleSpeechStarted(); break;
            case "input_speech_stopped":        HandleSpeechStopped(); break;
            case "response.created":            HandleResponseCreated(); break;
            case "response.audio.delta":        HandleAudioDelta(serverEvent); break;
            case "response.audio.done":         HandleAudioDone(); break;
            case "response.audio_transcript.delta":  HandleTranscriptDelta(serverEvent); break;
            case "response.audio_transcript.done":   HandleTranscriptDone(serverEvent); break;
            case "conversation.item.input_audio_transcription.completed": HandleUserTranscription(serverEvent); break;
            case "error":                       HandleError(serverEvent, json); break;
            case "response.done":               HandleResponseDone(serverEvent); break;
            case "response.output_item.added":  HandleToolCallStarted(serverEvent); break;
            case "response.function_call_arguments.delta": HandleToolCallDelta(serverEvent); break;
            case "response.function_call_arguments.done":  HandleToolCallDone(serverEvent); break;
            default:                            LogUnhandledEvent(serverEvent, json); break;
        }
    }

    // ── Dispatch handler methods ─────────────────────────────────────────────

    private void HandleServerReady()
    {
        UIMessageBus.PushSystem("Connected — listening.", readAloud: false);
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
    }

    private void HandleSpeechStarted()
    {
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  server VAD: speech started");
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
    }

    private void HandleSpeechStopped()
    {
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  server VAD: speech stopped (server will now process)");
        StatusChanged?.Invoke(this, new StatusEventArgs("Thinking…", ClientState.Thinking));
    }

    private void HandleResponseCreated()
    {
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  response.created (server generating audio)");
        _isModelSpeaking      = true;
        _isResponseInProgress = true;
        _responseHadAudio     = false;   // reset — will be set if audio deltas arrive
        StatusChanged?.Invoke(this, new StatusEventArgs("Speaking…", ClientState.Speaking));
    }

    private void HandleAudioDelta(JsonNode serverEvent)
    {
        var base64AudioData = serverEvent["delta"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(base64AudioData))
        {
            _responseHadAudio = true;
            if (!_firstAudioDelta)
            {
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  first audio chunk received ← playback starts here");
                _firstAudioDelta = true;
            }
            var audio = Convert.FromBase64String(base64AudioData);
            _audio?.PlayAudio(audio);
        }
    }

    private void HandleAudioDone()
    {
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  response.audio.done (all audio received)");
        _isResponseInProgress = false;
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
        // Keep mic gated briefly — audio is still playing back through the speaker.
        _ = Task.Run(async () =>
        {
            await Task.Delay(_audio?.GetPlaybackTailMs() ?? 500);
            _isModelSpeaking = false;
            AppLog.Info("[TIMING] mic re-enabled after playback tail");
        });
    }

    private void HandleUserTranscription(JsonNode serverEvent)
    {
        var userText = serverEvent["transcript"]?.GetValue<string>() ?? "";
        var redacted = userText.Length > 20 ? userText[..20] + "…" : userText;
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  STT transcription done: \"{redacted}\"");
        if (!string.IsNullOrEmpty(userText))
        {
            UIMessageBus.PushUser(userText, readAloud: false);
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs("You", userText));
        }
    }

    private void HandleTranscriptDelta(JsonNode serverEvent)
    {
        var delta = serverEvent["delta"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrEmpty(delta))
        {
            if (!_firstTranscriptDelta)
            {
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  first transcript delta received");
                _firstTranscriptDelta = true;
            }
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs("Assistant", delta, isDelta: true));
        }
    }

    private void HandleTranscriptDone(JsonNode serverEvent)
    {
        var full = serverEvent["transcript"]?.GetValue<string>() ?? "";
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  transcript done — full turn complete");
        if (!string.IsNullOrEmpty(full))
        {
            UIMessageBus.Push(MessageRole.Assistant, full, readAloud: false);
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs("Assistant", full));
        }
    }

    private void HandleError(JsonNode serverEvent, string json)
    {
        var errorData = serverEvent["error"];
        var code    = errorData?["code"]?.GetValue<string>()    ?? "unknown_code";
        var errorType = errorData?["type"]?.GetValue<string>()  ?? "unknown_type";
        var message = errorData?["message"]?.GetValue<string>() ?? "(no message)";
        var param   = errorData?["param"]?.GetValue<string>()   ?? "";
        var eventId = errorData?["event_id"]?.GetValue<string>() ?? serverEvent["event_id"]?.GetValue<string>() ?? "";

        AppLog.Error(
            $"[SERVER ERROR] type={errorType} code={code} message=\"{message}\"" +
            (string.IsNullOrEmpty(param)   ? "" : $" param={param}") +
            (string.IsNullOrEmpty(eventId) ? "" : $" event_id={eventId}") +
            $"\n  state: pttActive={_isPttActive} pttBytes={_pttAudioBytes}" +
            $" responseInProgress={_isResponseInProgress} modelSpeaking={_isModelSpeaking}" +
            $"\n  raw: {json}");

        UIMessageBus.PushSystem($"Error [{code}]: {message}", readAloud: false);
        StatusChanged?.Invoke(this, new StatusEventArgs($"Error: {message}", ClientState.Error));
    }

    private void HandleResponseDone(JsonNode serverEvent)
    {
        var status = serverEvent["response"]?["status"]?.GetValue<string>() ?? "?";
        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  response.done status={status}");
        _lastResponseFailed = (status == "failed");
        if (_lastResponseFailed)
        {
            var reason = serverEvent["response"]?["status_details"]?.ToJsonString() ?? "unknown";
            AppLog.Error($"response.done FAILED: {reason}");
        }
        _isResponseInProgress = false;

        // If the response had no audio (e.g. pure tool call), HandleAudioDone never fires,
        // so _isModelSpeaking would be stuck true and permanently gate the mic.
        // Safely release it here when no audio was produced.
        if (!_responseHadAudio)
        {
            _isModelSpeaking = false;
            AppLog.Info("response.done with no audio — mic gate released");
        }
    }

    private void HandleToolCallStarted(JsonNode serverEvent)
    {
        if (serverEvent["item"]?["type"]?.GetValue<string>() == "function_call")
        {
            var callId   = serverEvent["item"]?["call_id"]?.GetValue<string>() ?? "";
            var funcName = serverEvent["item"]?["name"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(callId))
            {
                _pendingCalls[callId] = (funcName, new StringBuilder());
                AppLog.Info($"Tool call started: {funcName} (id={callId})");
            }
        }
    }

    private void HandleToolCallDelta(JsonNode serverEvent)
    {
        var deltaCallId = serverEvent["call_id"]?.GetValue<string>() ?? "";
        if (_pendingCalls.TryGetValue(deltaCallId, out var pending))
            pending.Args.Append(serverEvent["delta"]?.GetValue<string>() ?? "");
    }

    private void HandleToolCallDone(JsonNode serverEvent)
    {
        var doneCid = serverEvent["call_id"]?.GetValue<string>() ?? "";
        if (_pendingCalls.TryRemove(doneCid, out var done))
            _ = Task.Run(() => ExecuteToolAndRespondAsync(doneCid, done.Name, done.Args.ToString()));
    }

    private static void LogUnhandledEvent(JsonNode serverEvent, string json)
    {
        var eventType = serverEvent["type"]?.GetValue<string>() ?? "?";
        if (!eventType.StartsWith("rate_limits") &&
            !eventType.StartsWith("session.") &&
            !eventType.StartsWith("conversation.item.created") &&
            !eventType.StartsWith("conversation.created"))
        {
            AppLog.Info($"[UNHANDLED EVENT] type={eventType} raw={json[..Math.Min(json.Length, 200)]}");
        }
    }

    // ── Tool execution (delegated to ToolRegistry) ─────────────────────────────

    private async Task ExecuteToolAndRespondAsync(string callId, string name, string argsJson)
    {
        var result = await _tools.ExecuteAsync(name, argsJson);
        AppLog.Info($"Tool '{name}' result: {result}");

        // Return the result to the model
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type    = "function_call_output",
                call_id = callId,
                output  = result,
            },
        });

        // If the result contains an error, instruct the model to read it aloud clearly.
        // For CLI/selection tools that succeed, stay silent — the user sees the result on screen.
        var isError = result.Contains("\"error\"");

        if (isError)
        {
            await SendJsonAsync(new
            {
                type = "response.create",
                response = new
                {
                    instructions = "The tool returned an error. Read the full error message aloud clearly so the user knows exactly what went wrong. Be specific — quote the error details. Suggest what they can do to fix it if you can infer it from the error.",
                    tool_choice  = "none",
                }
            });
        }
        else if (!ToolRegistry.IsSilentTool(name))
        {
            await SendJsonAsync(new
            {
                type = "response.create",
                response = new { tool_choice = "none" }
            });
        }
        // For successful CLI/selection commands: no response.create — stay silent
    }

    // ── Disconnect ───────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        _isUserDisconnected = true;
        _isConnected = false;
        await _cancellationTokenSource.CancelAsync();
        _audio?.StopCapture();
        _audio?.StopPlayback();
        if (_webSocket?.State == WebSocketState.Open)
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "User disconnected",
                                 CancellationToken.None);
        StatusChanged?.Invoke(this, new StatusEventArgs("Disconnected", ClientState.Idle));
    }

    // ── Always-on mode ────────────────────────────────────────────────────────

    /// <summary>
    /// Switches between always-on (server VAD) and PTT mode at runtime.
    /// Sends a session.update so the server immediately switches turn-detection strategy.
    /// </summary>
    public async Task SetAlwaysOnAsync(bool enabled)
    {
        _isAlwaysOn  = enabled;
        _isPttActive = false;                          // ensure PTT is released
        Interlocked.Exchange(ref _pttAudioBytes, 0);

        if (enabled)
        {
            await SendJsonAsync(new
            {
                type    = "session.update",
                session = new
                {
                    turn_detection = new
                    {
                        type                = "server_vad",
                        threshold           = _cfg.Voice.VadThreshold,
                        prefix_padding_ms   = 300,
                        silence_duration_ms = _cfg.Voice.SilenceDurationMs,
                        create_response     = true,
                    }
                }
            });
            AppLog.Info("Always-on: server VAD enabled");
            SetStatus("Always listening…", ClientState.Listening);
        }
        else
        {
            await SendJsonAsync(new
            {
                type    = "session.update",
                session = new { turn_detection = (object?)null }
            });
            // Discard any audio the server VAD was accumulating mid-turn
            await SendJsonAsync(new { type = "input_audio_buffer.clear" });
            AppLog.Info("Always-on: disabled, returned to PTT mode");
            SetStatus("PTT mode", ClientState.Listening);
        }
    }

    /// <summary>
    /// Updates the server VAD silence threshold live. Safe to call in both PTT and always-on mode:
    /// stores the value for next always-on activation; sends session.update immediately if
    /// currently in always-on mode so the change takes effect without reconnecting.
    /// </summary>
    public async Task UpdateVadSilenceAsync(int silenceDurationMs)
    {
        _cfg.Voice.SilenceDurationMs = silenceDurationMs;
        if (!_isAlwaysOn || !_isConnected) return;

        await SendJsonAsync(new
        {
            type    = "session.update",
            session = new
            {
                turn_detection = new
                {
                    type                = "server_vad",
                    threshold           = _cfg.Voice.VadThreshold,
                    prefix_padding_ms   = 300,
                    silence_duration_ms = silenceDurationMs,
                    create_response     = true,
                }
            }
        });
        AppLog.Info($"VAD silence_duration_ms updated to {silenceDurationMs}ms");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <param name="skipIfBusy">
    /// When true (used for high-frequency audio chunks), drops the message instead
    /// of queuing if a send is already in progress. Prevents buffer build-up.
    /// </param>
    private async Task SendJsonAsync(object payload, bool skipIfBusy = false)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        if (skipIfBusy)
        {
            if (!await _sendLock.WaitAsync(0)) return;
        }
        else
        {
            await _sendLock.WaitAsync(_cancellationTokenSource.Token);
        }

        try
        {
            if (_webSocket?.State != WebSocketState.Open) return;
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _cancellationTokenSource.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetStatus(string msg, ClientState state) =>
        StatusChanged?.Invoke(this, new StatusEventArgs(msg, state));

    /// <summary>
    /// Inject text into the conversation and have the Realtime API speak it aloud.
    /// Used for CLI narration so responses use the same natural voice.
    /// </summary>
    public async Task SpeakTextAsync(string text)
    {
        if (!_isConnected || _webSocket?.State != WebSocketState.Open)
        {
            AppLog.Warn("SpeakTextAsync: not connected, skipping.");
            return;
        }

        // Wait for any in-progress response to finish before injecting
        var waited = 0;
        while (_isResponseInProgress && waited < SpeakTextMaxWaitMs)
        {
            await Task.Delay(SpeakTextWaitIntervalMs);
            waited += SpeakTextWaitIntervalMs;
        }
        if (_isResponseInProgress)
        {
            AppLog.Warn($"SpeakTextAsync: response still in progress after {SpeakTextMaxWaitMs / 1000}s, proceeding anyway.");
        }

        AppLog.Info($"SpeakTextAsync: injecting {text.Length} chars for TTS");

        // Add the text as a user message so the model treats it as new input to read aloud
        // (using role "assistant" made the model think it already said this and generate a follow-up)
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type    = "message",
                role    = "user",
                content = new[]
                {
                    new { type = "input_text", text = $"[Copilot CLI Response — read aloud]: {text}" }
                }
            }
        });

        // Ask the model to generate audio for this text (with retry on server errors)
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _lastResponseFailed = false;

            await SendJsonAsync(new
            {
                type = "response.create",
                response = new
                {
                    modalities   = new[] { "audio", "text" },
                    instructions = "Read the Copilot CLI response aloud exactly as written. Be natural and conversational. Do not add commentary, follow-up questions, or extra words.",
                    tool_choice  = "none",
                }
            });

            // Wait for the response to complete
            var waitMs = 0;
            while (_isResponseInProgress && waitMs < 30000)
            {
                await Task.Delay(500);
                waitMs += 500;
            }

            if (!_lastResponseFailed)
            {
                AppLog.Info($"SpeakTextAsync: response succeeded on attempt {attempt}");
                break;
            }

            if (attempt < maxRetries)
            {
                var delay = attempt * 2000;
                AppLog.Warn($"SpeakTextAsync: response failed (attempt {attempt}/{maxRetries}), retrying in {delay}ms...");
                await Task.Delay(delay);
            }
            else
            {
                AppLog.Error($"SpeakTextAsync: all {maxRetries} attempts failed — narration not spoken.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellationTokenSource.CancelAsync();
        _audio?.Dispose();
        _webSocket?.Dispose();
        _cancellationTokenSource.Dispose();
    }
}
