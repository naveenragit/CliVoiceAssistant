using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VoiceAssistant;

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
    // Public events – fired on the ThreadPool; callers must marshal to UI thread.
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>?     StatusChanged;
    public event EventHandler<float>?               VolumeChanged;   // 0–1 RMS

    private readonly AppSettings          _cfg;
    private readonly TokenProvider?       _tokens;   // non-null when using Azure AD auth
    private readonly UserSettings?        _settings; // first-run user settings (overrides appsettings)
    private ClientWebSocket?              _ws;
    private CancellationTokenSource      _cts   = new();

    // Audio
    private WaveInEvent?                  _waveIn;
    private WaveOutEvent?                 _waveOut;
    private BufferedWaveProvider?         _playback;
    private static readonly WaveFormat   _fmt = new(24000, 16, 1);  // 24 kHz, 16-bit, mono

    private volatile bool _connected;
    private volatile bool _modelSpeaking;    // suppress mic echo during model output
    private volatile bool _pttActive;        // true only while PTT button/key is held
    private volatile bool _responseInProgress; // true between response.created and response.done

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
        if (!_connected) return;
        _pttActive     = true;
        _pttAudioBytes = 0;
        AppLog.Info("PTT start");
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
    }

    /// <summary>Called when the user releases the mic button / spacebar.</summary>
    public void StopPtt()
    {
        if (!_pttActive) return;
        _pttActive = false;

        var capturedBytes = _pttAudioBytes;

        // Too short — server requires ≥100ms. Just abort silently.
        if (capturedBytes < MinPttAudioBytes)
        {
            var ms = capturedBytes / 48; // bytes → ms at 24kHz 16-bit mono
            AppLog.Info($"PTT too short ({ms}ms, {capturedBytes} bytes) — skipping commit");
            StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
            return;
        }

        // Reset turn timer — T=0 is when the user finishes speaking
        _turnTimer.Restart();
        _firstAudioDelta      = false;
        _firstTranscriptDelta = false;
        AppLog.Info($"[TIMING] T=0 PTT released — {capturedBytes / 48}ms audio captured, committing");

        StatusChanged?.Invoke(this, new StatusEventArgs("Processing…", ClientState.Thinking));

        _ = Task.Run(async () =>
        {
            try
            {
                await SendJsonAsync(new { type = "input_audio_buffer.commit" });
                AppLog.Info("[TIMING] +0ms  input_audio_buffer.commit sent");

                // Only request a new response if the model isn't already responding
                if (!_responseInProgress)
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
    public RealtimeClient(AppSettings cfg)
    {
        _cfg      = cfg;
        _tokens   = null;
        _settings = null;
    }

    // First-run: uses UserSettings (endpoint + deployment) with Azure AD token
    public RealtimeClient(AppSettings cfg, UserSettings settings, TokenProvider tokens)
    {
        _cfg      = cfg;
        _settings = settings;
        _tokens   = tokens;
    }

    // ── Connect ─────────────────────────────────────────────────────────────

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();

        // Hard timeout: 60s to allow time for interactive browser sign-in
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token);

        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        AppLog.Info("ConnectAsync: building connection URI...");
        Uri uri;
        try
        {
            uri = await BuildUriAsync(linked.Token);
            AppLog.Info($"ConnectAsync: URI built successfully: {uri.Host}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            AppLog.Error("ConnectAsync: token acquisition timed out after 60s");
            throw new TimeoutException("Token acquisition timed out after 60 s. " +
                "Check your Azure AD sign-in or network connectivity.");
        }

        AppLog.Info($"Connecting to {uri.Host}…");
        StatusChanged?.Invoke(this, new StatusEventArgs("Connecting…", ClientState.Connecting));

        try
        {
            await _ws.ConnectAsync(uri, linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"WebSocket connection to {uri.Host} timed out after 20 s. " +
                "Verify the endpoint URL and your network.");
        }

        AppLog.Info("WebSocket connected.");
        _connected = true;

        // Send session config when not going through the proxy.
        if (_settings != null || _cfg.Mode == "azure")
            await SendSessionUpdateAsync();

        InitAudio();
        _ = Task.Run(ReceiveLoopAsync, _cts.Token);
        AppLog.Info("Listening.");
        StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
    }

    private async Task<Uri> BuildUriAsync(CancellationToken ct = default)
    {
        // ── Azure AD path (first-run / UserSettings) ───────────────────────
        if (_tokens != null && _settings != null)
        {
            var ep  = _settings.Endpoint.TrimEnd('/').Replace("https://", "wss://");
            var dep = Uri.EscapeDataString(_settings.DeploymentName);
            var url = $"{ep}/openai/realtime?api-version=2025-04-01-preview&deployment={dep}";

            AppLog.Info($"Endpoint: {ep}  Deployment: {dep}  AuthMode: {_settings.AuthMode}");

            if (_settings.AuthMode == "apikey")
            {
                var key = CredentialStore.RetrieveApiKey()
                    ?? throw new InvalidOperationException(
                        "API key not found in Windows Credential Manager. " +
                        "Re-open Settings (⚙) to re-enter your key.");
                _ws!.Options.SetRequestHeader("api-key", key);
                AppLog.Info("Using API key from Credential Manager.");
            }
            else
            {
                AppLog.Info("Fetching Azure AD bearer token…");
                var token = await _tokens.GetTokenAsync(ct);
                _ws!.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                AppLog.Info("Bearer token acquired.");
            }
            return new Uri(url);
        }

        // ── Legacy api-key path (appsettings.json azure mode) ─────────────
        if (_cfg.Mode == "azure")
        {
            var ep  = _cfg.Azure.Endpoint.TrimEnd('/').Replace("https://", "wss://");
            var url = $"{ep}/openai/realtime?api-version={_cfg.Azure.ApiVersion}&deployment={_cfg.Azure.Deployment}";
            AppLog.Info($"Legacy azure mode — endpoint: {ep}");
            _ws!.Options.SetRequestHeader("api-key", _cfg.Azure.ApiKey);
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
            // Disable server VAD — we use pure PTT (manual commit on release).
            // With VAD enabled, the server auto-commits audio mid-hold and creates
            // a race where our explicit commit finds an empty buffer.
            turn_detection           = (object?)null,
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
                " For EVERYTHING else — questions, commands, vague requests, unclear asks —" +
                " forward to Copilot CLI immediately. Do not think, do not interpret, do not" +
                " ask follow-up questions. Just forward it." +
                " When Copilot CLI responds, read the key information aloud concisely." +
                " Always use submit=true.",
            temperature  = 0.6,
            tools        = new object[] { CopilotCliTool.ToolDefinition, RememberToolDefinition },
            tool_choice  = "auto",
        },
    });

    // ── Audio I/O ────────────────────────────────────────────────────────────

    private void InitAudio()
    {
        // Playback
        _playback = new BufferedWaveProvider(_fmt)
        {
            BufferLength         = 1024 * 1024,
            DiscardOnBufferOverflow = true,
        };
        _waveOut = new WaveOutEvent { DesiredLatency = 80 };
        _waveOut.Init(_playback);
        _waveOut.Play();

        // Capture
        _waveIn = new WaveInEvent { WaveFormat = _fmt, BufferMilliseconds = 80 };
        _waveIn.DataAvailable += OnMicData;
        _waveIn.StartRecording();
    }

    private async void OnMicData(object? sender, WaveInEventArgs e)
    {
        // Only transmit while PTT is held and the model isn't speaking
        if (!_connected || _modelSpeaking || !_pttActive) return;
        if (_ws?.State != WebSocketState.Open) return;

        // Compute RMS for the volume meter
        float rms = ComputeRms(e.Buffer, e.BytesRecorded);
        VolumeChanged?.Invoke(this, rms);

        // Count bytes so StopPtt can verify minimum audio length
        _pttAudioBytes += e.BytesRecorded;

        try
        {
            await SendJsonAsync(new
            {
                type  = "input_audio_buffer.append",
                audio = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded),
            });
        }
        catch { /* ignore — connection may be closing */ }
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        var buf = new byte[65536];
        var sb  = new StringBuilder();

        while (_ws?.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buf, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                if (!result.EndOfMessage) continue;

                HandleEvent(sb.ToString());
                sb.Clear();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLog.Error($"ReceiveLoop error: {ex.GetType().Name}: {ex.Message}");
                UIMessageBus.PushSystem($"⚠ Connection lost: {ex.Message}", readAloud: true);
                break;
            }
        }

        _connected = false;
        SetStatus("Disconnected", ClientState.Idle);
        UIMessageBus.PushSystem("🔌 Disconnected from Realtime API. Click mic to reconnect.", readAloud: false);
    }

    // ── Event handler ─────────────────────────────────────────────────────────

    /// <summary>
    /// Estimates how many ms of audio remain in the playback buffer.
    /// Used to delay mic re-enable after response.audio.done so the model's
    /// own voice doesn't echo back as new input.
    /// Adds a fixed 500ms cushion for speaker propagation.
    /// </summary>
    private int GetPlaybackTailMs()
    {
        if (_playback == null) return 500;
        // bytes → ms: 24000 samples/s × 2 bytes/sample = 48 bytes/ms
        var bufferedMs = _playback.BufferedBytes / 48;
        var tailMs     = bufferedMs + 500;   // 500ms propagation cushion
        AppLog.Info($"[ECHO-GUARD] playback tail = {bufferedMs}ms buffered + 500ms cushion = {tailMs}ms");
        return tailMs;
    }

    private void HandleEvent(string json)
    {
        JObject? ev;
        try { ev = JObject.Parse(json); } catch { return; }

        switch (ev["type"]?.ToString())
        {
            // ── Proxy ready signal ──────────────────────────────────────────
            case "server.ready":
                UIMessageBus.PushSystem("Connected — listening.", readAloud: false);
                StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
                break;

            // ── Speech detection ────────────────────────────────────────────
            case "input_speech_started":
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  server VAD: speech started");
                StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
                break;

            case "input_speech_stopped":
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  server VAD: speech stopped (server will now process)");
                StatusChanged?.Invoke(this, new StatusEventArgs("Thinking…", ClientState.Thinking));
                break;

            // ── Model response ──────────────────────────────────────────────
            case "response.created":
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  response.created (server generating audio)");
                _modelSpeaking      = true;
                _responseInProgress = true;
                StatusChanged?.Invoke(this, new StatusEventArgs("Speaking…", ClientState.Speaking));
                break;

            case "response.audio.delta":
                var b64 = ev["delta"]?.ToString();
                if (!string.IsNullOrEmpty(b64))
                {
                    if (!_firstAudioDelta)
                    {
                        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  first audio chunk received ← playback starts here");
                        _firstAudioDelta = true;
                    }
                    var audio = Convert.FromBase64String(b64);
                    _playback?.AddSamples(audio, 0, audio.Length);
                }
                break;

            case "response.audio.done":
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  response.audio.done (all audio received)");
                _responseInProgress = false;
                StatusChanged?.Invoke(this, new StatusEventArgs("Listening…", ClientState.Listening));
                // Keep mic gated briefly — audio is still playing back through the speaker.
                // Re-enable after playback drains to prevent echo feeding back as new input.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(GetPlaybackTailMs());
                    _modelSpeaking = false;
                    AppLog.Info("[TIMING] mic re-enabled after playback tail");
                });
                break;

            // ── Transcripts ──────────────────────────────────────────────────
            case "conversation.item.input_audio_transcription.completed":
                var userText = ev["transcript"]?.ToString() ?? "";
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  STT transcription done: \"{userText}\"");
                if (!string.IsNullOrEmpty(userText))
                {
                    UIMessageBus.PushUser(userText, readAloud: false);
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs("You", userText));
                }
                break;

            case "response.audio_transcript.delta":
                var delta = ev["delta"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(delta))
                {
                    if (!_firstTranscriptDelta)
                    {
                        AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  first transcript delta received");
                        _firstTranscriptDelta = true;
                    }
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs("Assistant", delta, isDelta: true));
                }
                break;

            case "response.audio_transcript.done":
                var full = ev["transcript"]?.ToString() ?? "";
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  transcript done — full turn complete");
                if (!string.IsNullOrEmpty(full))
                {
                    UIMessageBus.Push(MessageRole.Assistant, full, readAloud: false);
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs("Assistant", full));
                }
                break;

            // ── Errors ───────────────────────────────────────────────────────
            case "error":
            {
                var errObj  = ev["error"];
                var code    = errObj?["code"]?.ToString()    ?? "unknown_code";
                var errType = errObj?["type"]?.ToString()    ?? "unknown_type";
                var message = errObj?["message"]?.ToString() ?? "(no message)";
                var param   = errObj?["param"]?.ToString()   ?? "";
                var eventId = errObj?["event_id"]?.ToString() ?? ev["event_id"]?.ToString() ?? "";

                AppLog.Error(
                    $"[SERVER ERROR] type={errType} code={code} message=\"{message}\"" +
                    (string.IsNullOrEmpty(param)   ? "" : $" param={param}") +
                    (string.IsNullOrEmpty(eventId) ? "" : $" event_id={eventId}") +
                    $"\n  state: pttActive={_pttActive} pttBytes={_pttAudioBytes}" +
                    $" responseInProgress={_responseInProgress} modelSpeaking={_modelSpeaking}" +
                    $"\n  raw: {json}");

                UIMessageBus.PushSystem($"Error [{code}]: {message}", readAloud: false);
                StatusChanged?.Invoke(this, new StatusEventArgs($"Error: {message}", ClientState.Error));
                break;
            }

            // ── Response complete (fires after audio+tool calls finish) ───────
            case "response.done":
                AppLog.Info($"[TIMING] +{_turnTimer.ElapsedMilliseconds}ms  response.done " +
                    $"status={ev["response"]?["status"]?.ToString() ?? "?"}");
                _responseInProgress = false;
                break;

            // ── Function / tool calls ─────────────────────────────────────────
            // Step 1 — model starts a function call output item
            case "response.output_item.added":
                if (ev["item"]?["type"]?.ToString() == "function_call")
                {
                    var callId   = ev["item"]?["call_id"]?.ToString() ?? "";
                    var funcName = ev["item"]?["name"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(callId))
                    {
                        _pendingCalls[callId] = (funcName, new StringBuilder());
                        AppLog.Info($"Tool call started: {funcName} (id={callId})");
                    }
                }
                break;

            // Step 2 — arguments arrive as streaming deltas
            case "response.function_call_arguments.delta":
                var dcid = ev["call_id"]?.ToString() ?? "";
                if (_pendingCalls.TryGetValue(dcid, out var pending))
                    pending.Args.Append(ev["delta"]?.ToString() ?? "");
                break;

            // Step 3 — arguments complete; execute the tool
            case "response.function_call_arguments.done":
                var doneCid = ev["call_id"]?.ToString() ?? "";
                if (_pendingCalls.TryRemove(doneCid, out var done))
                    _ = Task.Run(() => ExecuteToolAndRespondAsync(doneCid, done.Name, done.Args.ToString()));
                break;

            default:
                // Log events we don't explicitly handle — useful for spotting
                // unexpected server events during debugging.
                var evType = ev["type"]?.ToString() ?? "?";
                if (!evType.StartsWith("rate_limits") &&
                    !evType.StartsWith("session.") &&
                    !evType.StartsWith("conversation.item.created") &&
                    !evType.StartsWith("conversation.created"))
                {
                    AppLog.Info($"[UNHANDLED EVENT] type={evType} raw={json[..Math.Min(json.Length, 200)]}");
                }
                break;
        }
    }

    // ── Tool execution ────────────────────────────────────────────────────────

    private async Task ExecuteToolAndRespondAsync(string callId, string name, string argsJson)
    {
        AppLog.Info($"Executing tool '{name}' args={argsJson}");
        UIMessageBus.PushTool($"Calling tool: {name}", readAloud: false);

        string result;
        try
        {
            result = name switch
            {
                CopilotCliTool.FunctionName => await ExecuteCopilotCliToolAsync(argsJson),
                "remember_fact"             => ExecuteRememberTool(argsJson),
                _                           => $"{{ \"error\": \"unknown tool '{name}'\" }}"
            };
        }
        catch (Exception ex)
        {
            result = $"{{ \"error\": \"{ex.Message}\" }}";
            AppLog.Error($"Tool '{name}' threw: {ex.Message}");
        }

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
        // Otherwise just let it continue naturally.
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
        else
        {
            // tool_choice = "none" prevents the model from calling another tool
            // (which would create a feedback loop sending CLI output back to CLI).
            // It will only generate speech to read the result aloud.
            await SendJsonAsync(new
            {
                type = "response.create",
                response = new { tool_choice = "none" }
            });
        }
    }

    private static async Task<string> ExecuteCopilotCliToolAsync(string argsJson)
    {
        JObject args;
        try { args = JObject.Parse(argsJson); }
        catch { return "{ \"error\": \"invalid arguments JSON\" }"; }

        var message = args["message"]?.ToString() ?? "";
        var submit  = args["submit"]?.Value<bool>() ?? true;
        return await CopilotCliTool.SendAsync(message, submit);
    }

    // ── Remember tool ────────────────────────────────────────────────────────

    private static readonly object RememberToolDefinition = new
    {
        type        = "function",
        name        = "remember_fact",
        description = "Persist a fact about the user (name, preferences, etc.) to long-term memory. " +
                      "These facts survive across sessions, conversation resets, and app restarts. " +
                      "Use when the user says 'remember that...', 'my name is...', 'I prefer...', etc.",
        parameters  = new
        {
            type       = "object",
            properties = new
            {
                key = new
                {
                    type        = "string",
                    description = "Short label for the fact, e.g. 'name', 'preferred_language', 'team'.",
                },
                value = new
                {
                    type        = "string",
                    description = "The fact to remember, e.g. 'Naveen', 'Python', 'Platform Engineering'.",
                },
            },
            required = new[] { "key", "value" },
        },
    };

    private static string ExecuteRememberTool(string argsJson)
    {
        JObject args;
        try { args = JObject.Parse(argsJson); }
        catch { return "{ \"error\": \"invalid arguments JSON\" }"; }

        var key   = args["key"]?.ToString()?.Trim() ?? "";
        var value = args["value"]?.ToString()?.Trim() ?? "";

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            return "{ \"error\": \"key and value are both required\" }";

        UserMemory.Set(key, value);
        UIMessageBus.PushSystem($"📝 Remembered: {key} = {value}", readAloud: false);
        return $"{{ \"success\": true, \"key\": \"{key}\", \"value\": \"{value}\" }}";
    }

    // ── Disconnect ───────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        _connected = false;
        await _cts.CancelAsync();
        _waveIn?.StopRecording();
        _waveOut?.Stop();
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "User disconnected",
                                 CancellationToken.None);
        StatusChanged?.Invoke(this, new StatusEventArgs("Disconnected", ClientState.Idle));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SendJsonAsync(object payload)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
    }

    private void SetStatus(string msg, ClientState state) =>
        StatusChanged?.Invoke(this, new StatusEventArgs(msg, state));

    private static float ComputeRms(byte[] buf, int length)
    {
        double sum = 0;
        int samples = length / 2;
        for (int i = 0; i < length - 1; i += 2)
        {
            short s = (short)(buf[i] | (buf[i + 1] << 8));
            sum += (double)s * s;
        }
        return samples > 0 ? (float)Math.Sqrt(sum / samples) / 32768f : 0f;
    }

    /// <summary>
    /// Inject text into the conversation and have the Realtime API speak it aloud.
    /// Used for CLI narration so responses use the same natural voice.
    /// </summary>
    public async Task SpeakTextAsync(string text)
    {
        if (!_connected || _ws?.State != WebSocketState.Open)
        {
            AppLog.Warn("SpeakTextAsync: not connected, skipping.");
            return;
        }

        AppLog.Info($"SpeakTextAsync: injecting {text.Length} chars for TTS");

        // Add the text as an assistant message in the conversation
        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type    = "message",
                role    = "assistant",
                content = new[]
                {
                    new { type = "input_text", text = text }
                }
            }
        });

        // Ask the model to generate audio for this text
        await SendJsonAsync(new
        {
            type = "response.create",
            response = new
            {
                modalities   = new[] { "audio" },
                instructions = $"Read the following text aloud exactly as written, naturally and conversationally. Do not add commentary or extra words:\n\n{text}",
                tool_choice  = "none",
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _waveIn?.Dispose();
        _waveOut?.Dispose();
        _ws?.Dispose();
        _cts.Dispose();
    }
}
