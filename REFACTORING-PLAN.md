# Voice Assistant v4 — Refactoring Plan

## Part 1: Best Practices Checklist

This section lists best practices for a project of this nature (WinForms + real-time audio + WebSocket + Win32 interop) and evaluates the current codebase against each.

---

### 1. Architecture & Separation of Concerns

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 1.1 | **UI logic separated from business logic** — Forms should be thin shells delegating to services. | `MainForm.cs` is ~750 lines mixing layout construction, event wiring, connection orchestration, spinner animation, chat rendering, token refresh event handling, PTT state management, and drag-to-move logic — all in one class. | ❌ Violation |
| 1.2 | **No static mutable state for service wiring** — Dependencies should be injected, not assigned to static properties. | `CopilotCliTool` is a fully static class with static mutable properties (`Terminal`, `OnDelta`, `OnCommandStarted`, `OnCommandCompleted`, `InstructionSuffix`, `AutoSubmit`, `ResumeSessionId`). MainForm assigns these at runtime. | ❌ Violation |
| 1.3 | **Message bus should not be static** — Testability and lifetime scoping require instance-based pub/sub. | `UIMessageBus` is a static class. Impossible to test consumers in isolation or scope bus lifetime. | ⚠️ Moderate |
| 1.4 | **Single Responsibility Principle** — Each class handles one concern. | `RealtimeClient` handles WebSocket management, audio I/O (capture + playback), PTT lifecycle, tool execution, remember-tool logic, session configuration, and server event dispatching (~700 lines, monolithic `HandleEvent` switch). | ❌ Violation |
| 1.5 | **Tool definitions co-located with tool implementations** — Tool schema and execution should live together. | `remember_fact` tool definition and execution live inside `RealtimeClient`, not in a dedicated tool class. `CopilotCliTool` has its own definition but is called from `RealtimeClient`. | ⚠️ Inconsistent |

### 2. Dependency Injection & Testability

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 2.1 | **Constructor injection for dependencies** — Classes receive their dependencies via constructors. | `MainForm` creates `RealtimeClient` inline in `ConnectAsync()`. `CopilotCliTool` is static — no injection possible. `AppLog` is static. `UIMessageBus` is static. | ❌ Violation |
| 2.2 | **Interfaces for external dependencies** — WebSocket, audio, credential store, file I/O should be behind interfaces for testing. | Zero interfaces in the project. `ClientWebSocket`, `WaveInEvent`, `WaveOutEvent`, `SpeechSynthesizer`, `PasswordVault`, `Process` are all used directly with no abstraction seam. | ❌ Violation |
| 2.3 | **Unit tests exist for core logic** — Tool execution, token refresh, message formatting should be testable. | No test project exists. Nothing is testable due to static coupling and concrete dependencies. | ❌ Missing |

### 3. Resource Management & Disposal

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 3.1 | **IDisposable/IAsyncDisposable implemented consistently** — All classes owning unmanaged resources implement disposal. | `RealtimeClient` implements `IAsyncDisposable` but `DisposeAsync` doesn't dispose `_waveIn`/`_waveOut`. `EmbeddedTerminal` implements `IDisposable`. `VoiceOutput` implements `IAsyncDisposable`. `MainForm` never disposes `_tts`, `_client`, `_terminal`, `_tokenRefresh`. | ❌ Violation |
| 3.2 | **CancellationToken passed through the stack** — All async operations accept cancellation. | `ConnectAsync` creates internal `CancellationTokenSource` but most internal methods don't propagate tokens (e.g., `SendTextAsync`, `RunPromptModeAsync`). | ⚠️ Partial |
| 3.3 | **No fire-and-forget `Task.Run` without error handling** — Unobserved exceptions crash the process in .NET 8. | Multiple `_ = Task.Run(...)` calls in `RealtimeClient` (receive loop, tool execution, echo guard delay) and `MainForm` (`StartEmbeddedTerminalAsync`). Some have try/catch, some don't. | ⚠️ Risky |
| 3.4 | **ToolTip instances are disposed** — WinForms ToolTip is IDisposable; creating new ones per click leaks handles. | `SetMicState()` creates `new ToolTip()` every time it's called (per connect/disconnect). `BuildUI` also creates loose ToolTip instances. | ❌ Leak |

### 4. Error Handling & Resilience

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 4.1 | **Reconnection strategy for WebSocket drops** — Transient network failures should auto-reconnect with backoff. | No automatic reconnection. If the WebSocket drops, the receive loop exits and a message says "click mic to reconnect." | ⚠️ Missing |
| 4.2 | **Structured error types** — Distinguish transient vs. fatal errors in tool execution, auth, and WebSocket. | All errors are caught as bare `Exception` with string messages. No error taxonomy. | ⚠️ Moderate |
| 4.3 | **Graceful degradation** — If the terminal fails to embed, the app should still work in prompt-mode only. | `CopilotCliTool.SendAsync` already falls back to prompt mode if terminal isn't running. Good. | ✅ OK |
| 4.4 | **Timeout on external process launches** — `Process.Start` with no timeout can hang. | `EmbeddedTerminal.WaitForWindowAsync` has a 15s timeout. `RunPromptModeAsync` has no timeout on `proc.WaitForExit`. | ⚠️ Partial |

### 5. Concurrency & Thread Safety

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 5.1 | **UI thread marshalling is consistent** — All UI updates go through `Invoke`/`BeginInvoke`. | `MainForm.OnMessagePushed` marshals correctly. `OnTokenExpiring`/`OnTokenRefreshed` use `BeginInvoke`. But `OnVoiceCommandStarted`/`OnVoiceDelta`/`OnVoiceCommandCompleted` duplicate the pattern with manual Invoke checks. | ⚠️ Inconsistent |
| 5.2 | **Volatile fields used correctly** — `volatile` doesn't guarantee atomicity for compound read-then-write. | `_pttActive`, `_connected`, `_modelSpeaking`, `_responseInProgress` are volatile booleans. Used in simple flag-check patterns — acceptable for booleans but `_pttAudioBytes` (an int incremented from callback) is NOT volatile or locked. | ⚠️ Minor race |
| 5.3 | **No async void except event handlers** — `async void` swallows exceptions silently. | `OnLoad`, `OnMicClick`, `OnSetupConfirmed`, `OnTokenRefreshed`, `OnMicData` are `async void` — these are all event handlers, so acceptable. | ✅ OK |

### 6. Configuration & Secrets

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 6.1 | **No secrets in source files** — API keys, endpoints belong in user config or environment. | `appsettings.json` has placeholder `"YOUR-API-KEY-HERE"` and `"YOUR-RESOURCE"` — not real secrets but the _structure_ encourages pasting keys into a source-controlled file. | ⚠️ Risky pattern |
| 6.2 | **Single source of truth for configuration** — Settings shouldn't be split across multiple orthogonal systems. | Configuration flows from 3 sources: `appsettings.json` (bundled), `UserSettings` (%APPDATA%), and `CredentialStore` (vault). `RealtimeClient` has two constructors for two config paths. | ⚠️ Complex |
| 6.3 | **Validated configuration at startup** — Invalid settings should fail fast with clear errors. | `UserSettings.IsValidEndpoint` exists but is only called in the setup dialog. If someone edits `settings.json` manually and corrupts it, `ConnectAsync` will throw a confusing URI error. | ⚠️ Partial |

### 7. Logging & Observability

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 7.1 | **Structured logging** — Use log levels, correlation IDs, and structured fields (not string concat). | `AppLog` uses simple string concatenation: `$"[TIMING] +{ms}ms text"`. No structured fields, no correlation IDs. | ⚠️ Basic |
| 7.2 | **Log rotation is robust** — Deleting the entire log on size threshold loses history. | `AppLog` static constructor deletes the log if >200 KB. All previous session data is lost. Should use rolling files or tail truncation. | ⚠️ Fragile |
| 7.3 | **Sensitive data not logged** — Tokens, keys, and user speech should be redacted. | Bearer tokens are not logged (only `"Bearer token acquired"`). User speech transcripts ARE logged in full. | ⚠️ Privacy concern |

### 8. Code Quality & Maintainability

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 8.1 | **Magic numbers extracted to named constants** — Colors, sizes, delays should be named. | Colors are named constants (good). But delay values are magic: `Task.Delay(100)`, `Task.Delay(50)`, `Task.Delay(5)`, `Task.Delay(500)`, buffer size `65536`, audio format `24000` repeated. | ⚠️ Partial |
| 8.2 | **JSON construction uses typed models** — Building JSON via anonymous objects is fragile and hard to refactor. | All WebSocket messages and tool definitions use deeply nested anonymous objects. Tool response JSON is hand-concatenated strings: `$"{{ \"success\": true, ... }}"`. | ❌ Fragile |
| 8.3 | **Consistent JSON library** — A single JSON library should be used across the project. | Both `Newtonsoft.Json` AND `System.Text.Json` are used. `RealtimeClient` uses Newtonsoft. `CopilotCliTool` uses System.Text.Json. `UserMemory` uses System.Text.Json. `UserSettings` uses Newtonsoft. | ❌ Inconsistent |
| 8.4 | **No hardcoded paths** — File paths should be derived from well-known folders or config. | `FindProjectRoot()` has a hardcoded fallback: `Path.Combine(UserProfile, "CopilotCLISubFolder")`. | ⚠️ Minor |

### 9. Security

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 9.1 | **API keys never in memory longer than needed** — Secrets should be short-lived. | API key retrieved from vault is passed directly as a header — not stored in a field. Good. | ✅ OK |
| 9.2 | **WebSocket uses TLS** — All connections must be wss://. | `BuildUriAsync` converts `https://` to `wss://`. Proxy mode uses `ws://localhost` which is acceptable for local dev. | ✅ OK |
| 9.3 | **Process execution is safe** — No shell injection from voice-transcribed text. | `EmbeddedTerminal` uses `PostMessage(WM_CHAR)` for character injection — not shell execution. `RunPromptModeAsync` passes user text through `EscapeArg()`. | ✅ OK |

### 10. Naming Conventions

> A name should tell the reader what a thing **is** or **does** — without requiring them to read the implementation.

| # | Best Practice | Current State | Verdict |
|---|--------------|---------------|---------|
| 10.1 | **Class names describe their responsibility** — A class name should communicate what the class owns and does, not just what it is in the abstract. | `RealtimeClient` doesn't indicate it is an Azure OpenAI Realtime API client. `McpInstaller` isn't an installer — it registers config entries. `NarrationServer` doesn't convey it bridges Copilot CLI text to spoken audio. `AppLog` is too generic for a file-based session logger. `SetupDialog`/`SetupOverlay` don't say they configure Azure connection settings. | ❌ Violation |
| 10.2 | **Method names are verb phrases that describe the action** — Avoid `Handle`, `Init`, `Process`, `Do`, `Run` without qualification; they describe mechanics, not intent. | `HandleEvent` should be `DispatchRealtimeServerEvent`. `InitAudio` should be `InitializeAudioCaptureAndPlayback`. `ComputeRms` hides the domain term; should be `ComputeAudioRmsVolume`. | ❌ Violation |
| 10.3 | **Boolean fields/properties use an `is`/`has`/`can` prefix** — Reads as a natural question: `if (_isConnected)` vs `if (_connected)`. | `_connected`, `_pttActive`, `_modelSpeaking`, `_responseInProgress`, `_spinnerActive`, `_dragging`, `_pttHeld`, `_suppressed`, `_authenticated` — none use the `is` prefix. | ❌ Violation |
| 10.4 | **Field names spell out abbreviations** — Abbreviations like `_ws`, `_cts`, `_fmt`, `_tts`, `_ptt` require the reader to guess the domain context. | `_ws` (WebSocket), `_cts` (CancellationTokenSource), `_fmt` (WaveFormat), `_tts` (VoiceOutput), `_waveIn` / `_waveOut` (microphone / speaker device), `_playback` (audio buffer). All used across multiple files. | ❌ Violation |
| 10.5 | **Local variables and parameters are descriptive** — Single-letter and heavily abbreviated locals impede readability in non-trivial methods. | `sb` (StringBuilder), `buf` (byte buffer), `psi` (ProcessStartInfo), `proc` (Process), `sid` (session ID), `b64` (base64 audio), `dcid` (call ID), `ep` (endpoint), `dep` (deployment), `ctx` (HttpListenerContext), `wsCtx` (WebSocketContext), `ack` (acknowledgement) — found across `RealtimeClient`, `CopilotCliTool`, `NarrationServer`. | ❌ Violation |

--- 

## Part 2: New Components (Added Since Initial Analysis)

The following files were added to the codebase after the initial best-practice audit. They are not mentioned in the refactoring items but are part of the active codebase.

| File | Purpose | Quality Notes |
|------|---------|---------------|
| `NarrationServer.cs` | Local WebSocket server (port 9877) that receives narration text from the Copilot CLI MCP tool and speaks it via the Realtime API. | ✅ Implements `IAsyncDisposable`. Clean and focused. |
| `TokenRefreshService.cs` | Proactively monitors Azure AD token expiry and fires `TokenExpiring` / `TokenRefreshed` events so `RealtimeClient` can update its auth without a full reconnect. | ✅ Well-designed event model. `MainForm` subscribes and delegates to `RealtimeClient`. |
| `SetupOverlay.cs` | Inline settings panel embedded in `MainForm`. Handles endpoint, deployment name, and auth mode (AAD vs API key) configuration. Stores secrets in Credential Manager. | ✅ Functional. Extensive UI state management. No obvious issues. |
| `McpInstaller.cs` | Registers/unregisters the narration MCP tool in the Copilot CLI system config on connect/disconnect. | ✅ Focused, functional. |
| `CredentialStore.cs` | Wraps Windows Credential Manager (`PasswordVault`) to securely store and retrieve API keys. | ✅ Clean, minimal, correct. |
| `TokenProvider.cs` | Azure AD token acquisition with an `AzureCli → InteractiveBrowser` fallback chain. Handles MSAL cache. | ✅ Well-implemented. |
| `UIMessage.cs` | Simple data record for the message bus (`Role`, `Text`, `Timestamp`, `ReadAloud`). | ✅ Minimal and clean. |

---

## Part 3: Refactoring Plan

Based on the best practice violations identified above, here is the prioritized refactoring plan organized by phase.

---

## Current Implementation Status

> Last reviewed: 2026-03-26. Legend: ✅ Done · ⚠️ Partial · ❌ Not Started

| # | Item | Status | Notes |
|---|------|--------|-------|
| 1.1 | Fix resource disposal chain | ✅ Done | `RealtimeClient` disposes audio devices. `MainForm.OnFormClosing` disposes `_tokenRefresh`, `_tooltip`, terminal, narration, tts. |
| 1.2 | Fix ToolTip handle leak | ✅ Done | Single `_tooltip` field reused in `MakeTitleButton` and `SetMicState`. Disposed in `OnFormClosing`. |
| 1.3 | Add timeout to prompt-mode process | ✅ Done | 30s `CancellationTokenSource` timeout with `Task.WhenAny` in `RunPromptModeAsync`. |
| 1.4 | Use `Interlocked` for `_pttAudioBytes` | ✅ Done | `Interlocked.Add` for increment, `Interlocked.Exchange` for reset, `Volatile.Read` for reads. |
| 2.1 | Extract `AudioManager` | ✅ Done | New `AudioManager.cs` — owns mic capture, speaker playback, RMS volume, playback buffer. `RealtimeClient` delegates via events. |
| 2.2 | Extract `ToolRegistry` | ✅ Done | New `ToolRegistry.cs` — owns tool definitions, dispatch, remember/select_option/copilot_cli tool logic. |
| 2.3 | Extract `RealtimeEventRouter` | ✅ Done | `DispatchRealtimeServerEvent` refactored into 14 named handler methods within `RealtimeClient`. |
| 2.4 | `CopilotCliTool` → instance class | ✅ Done | Converted to instance class. Created in `Program.cs`, injected into `MainForm` and `ToolRegistry`. Events replace static delegates. |
| 2.5 | Extract `ChatRenderer` / `PttController` | ✅ Done | New `ChatRenderer.cs` (message rendering, spinner) and `PttController.cs` (press/release state, events). `MainForm` delegates to both. |
| 3.1 | Unify JSON library to `System.Text.Json` | ✅ Done | All `Newtonsoft.Json` replaced. Package removed from csproj. |
| 3.2 | Typed response models for tool JSON | ✅ Done | File-scoped records replace hand-built JSON strings in `RealtimeClient` and `CopilotCliTool`. |
| 3.3 | Extract magic numbers to constants | ✅ Done | 12 named constants added to `RealtimeClient`. |
| 3.4 | `RunOnUI` helper for UI marshalling | ✅ Done | Helper method added. 7+ duplicated patterns replaced. |
| 3.5 | Remove hardcoded project root fallback | ✅ Done | `"CopilotCLISubFolder"` fallback removed from `FindProjectRoot()`. |
| 3.6 | Rename classes to reflect responsibility | ❌ Not Started | See §10.1 above. |
| 3.7 | Rename ambiguous method names | ✅ Done | `HandleEvent`→`DispatchRealtimeServerEvent`, `InitAudio`→`InitializeAudioCaptureAndPlayback`, `ComputeRms`→`ComputeAudioRmsVolume`, `pressEnter`→`submitWithEnter`. |
| 3.8 | Add `is`/`has` prefix to boolean fields | ✅ Done | 10 booleans renamed across `MainForm`, `RealtimeClient`, `VoiceOutput`, `SetupOverlay`. |
| 3.9 | Spell out abbreviated field names | ✅ Done | `_ws`→`_webSocket`, `_cts`→`_cancellationTokenSource`, `_fmt`→`_audioWaveFormat`, `_tts`→`_voiceOutputQueue`, etc. |
| 3.10 | Rename abbreviated locals and parameters | ✅ Done | All abbreviated locals renamed across `RealtimeClient`, `CopilotCliTool`, `NarrationServer`. |
| 4.1 | Auto-reconnect with exponential backoff | ✅ Done | Exponential backoff (1s→30s cap, max 5 retries). `_isUserDisconnected` flag distinguishes user disconnect from network drop. |
| 4.2 | Improve log rotation | ✅ Done | Rolling rotation: archive to `app.log.1` before starting fresh, instead of deleting. |
| 4.3 | Redact user speech from logs | ✅ Done | STT transcription truncated to first 20 chars + "…" in log output. |
| 4.4 | Validate settings at startup | ✅ Done | `UserSettings.Validate()` checks endpoint/deployment; resets `IsConfigured` if invalid. Called in `Program.Main`. |
| 5.1 | Interfaces for testability | ❌ Not Started | Zero interfaces; all concrete dependencies. |
| 5.2 | Add test project | ❌ Not Started | No test project or test files. |
| 5.3 | `UIMessageBus` → instance-based | ❌ Not Started | Still a fully static class. |
| 5.4 | Unified configuration model | ❌ Not Started | `AppSettings` and `UserSettings` remain separate. `RealtimeClient` has two constructors. |

---

### Phase 1: Critical Fixes (Safety & Resource Leaks)

> Fix bugs and resource leaks that affect stability in production.

#### 1.1 — Fix resource disposal chain · ✅ Done
**Addresses**: 3.1  
**Files**: `MainForm.cs`, `RealtimeClient.cs`  
**Current State**: `RealtimeClient.DisposeAsync` now correctly disposes `_waveIn`, `_waveOut`, `_cts`, and `_ws`. `MainForm.OnFormClosing` disposes `_spinnerTimer`, `_terminal`, `_narration`, and `_tts`. However `_tokenRefresh` is not explicitly disposed in `OnFormClosing` — it is only stopped via `DisconnectAsync`.  
**Remaining**: Add `_tokenRefresh?.Stop(); _tokenRefresh?.Dispose();` to `MainForm.OnFormClosing`.  
**What**:
- `MainForm.OnFormClosing` must dispose `_tts` (IAsyncDisposable), `_client`, `_terminal`, and `_tokenRefresh`.
- `RealtimeClient.DisposeAsync` must dispose `_waveIn` and `_waveOut` (NAudio resources).
- Add null-checks before disposal.

**Change**:
```csharp
// MainForm.OnFormClosing → add:
_tokenRefresh?.Stop();
_tokenRefresh?.Dispose();
_terminal?.Dispose();
if (_client != null) await _client.DisposeAsync();
await _tts.DisposeAsync();

// RealtimeClient.DisposeAsync → add:
_waveIn?.StopRecording();
_waveIn?.Dispose();
_waveOut?.Stop();
_waveOut?.Dispose();
```

#### 1.2 — Fix ToolTip handle leak · ✅ Done
**Addresses**: 3.4  
**Files**: `MainForm.cs`  
**What**:
- Create a single `ToolTip` instance as a field in `MainForm`.
- Reuse it in `SetMicState()` and `BuildUI()` instead of allocating new ones.
- Dispose it in `OnFormClosing`.

#### 1.3 — Add timeout to prompt-mode process · ✅ Done
**Addresses**: 4.4  
**Files**: `CopilotCliTool.cs`  
**What**:
- Add a 30-second timeout to `RunPromptModeAsync` using `proc.WaitForExitAsync(ct)` with a CancellationTokenSource timeout.
- Kill the process if it exceeds the timeout.

#### 1.4 — Use `Interlocked` for `_pttAudioBytes` · ✅ Done
**Addresses**: 5.2  
**Files**: `RealtimeClient.cs`  
**What**:
- Replace `_pttAudioBytes += e.BytesRecorded` with `Interlocked.Add(ref _pttAudioBytes, e.BytesRecorded)`.
- Replace reads with `Interlocked.CompareExchange(ref _pttAudioBytes, 0, 0)` or `Volatile.Read`.

---

### Phase 2: Structural Refactoring (Separation of Concerns)

> Break apart monolithic classes into focused, testable components.

#### 2.1 — Extract `AudioManager` from `RealtimeClient` · ❌ Not Started
**Addresses**: 1.4  
**Files**: New `AudioManager.cs`, modify `RealtimeClient.cs`  
**What**:
- Move `InitAudio()`, `OnMicData()`, `ComputeRms()`, `_waveIn`, `_waveOut`, `_playback`, `_fmt`, and `GetPlaybackTailMs()` into a new `AudioManager` class.
- Expose methods: `StartCapture()`, `StopCapture()`, `PlayAudio(byte[])`, `GetBufferedTailMs()`.
- Expose events: `AudioCaptured(byte[] pcm)`, `VolumeChanged(float rms)`.
- `RealtimeClient` owns an `AudioManager` instance and wires events.
- `AudioManager` implements `IDisposable`.

#### 2.2 — Extract `ToolRegistry` / `ToolDispatcher` · ❌ Not Started
**Addresses**: 1.4, 1.5  
**Files**: New `ToolRegistry.cs`, modify `RealtimeClient.cs`, `CopilotCliTool.cs`  
**What**:
- Create a `ToolRegistry` that holds tool definitions and dispatch logic.
- Move `RememberToolDefinition`, `ExecuteRememberTool()`, `ExecuteToolAndRespondAsync()`, and `ExecuteCopilotCliToolAsync()` out of `RealtimeClient`.
- `ToolRegistry` exposes: `IReadOnlyList<object> Definitions`, `Task<string> ExecuteAsync(string name, string argsJson)`.
- `RealtimeClient` delegates `response.function_call_arguments.done` to `ToolRegistry`.

#### 2.3 — Extract `WebSocketEventRouter` from `RealtimeClient.HandleEvent` · ❌ Not Started
**Addresses**: 1.4  
**Files**: New `RealtimeEventRouter.cs`, modify `RealtimeClient.cs`  
**What**:
- The `HandleEvent(string json)` method is a 200+ line switch statement. Extract it into a `RealtimeEventRouter` class.
- Each case becomes a handler method.
- The router raises domain events (e.g., `AudioDeltaReceived`, `TranscriptCompleted`, `ToolCallRequested`, `ErrorReceived`).
- `RealtimeClient` subscribes to these events instead of inline processing.

#### 2.4 — Convert `CopilotCliTool` from static to instance class · ❌ Not Started
**Addresses**: 1.2, 2.1  
**Files**: `CopilotCliTool.cs`, `MainForm.cs`, `Program.cs`  
**What**:
- Make `CopilotCliTool` a regular class with constructor injection for `EmbeddedTerminal`.
- Replace static properties with constructor parameters and instance fields.
- Remove the static delegate properties (`OnDelta`, `OnCommandStarted`, `OnCommandCompleted`); use events instead.
- Create the instance in `Program.cs` and inject it into `MainForm` and `RealtimeClient`.

#### 2.5 — Slim down `MainForm` — Extract `ChatRenderer` and `PttController` · ❌ Not Started
**Addresses**: 1.1  
**Files**: New `ChatRenderer.cs`, new `PttController.cs`, modify `MainForm.cs`  
**What**:

**ChatRenderer**:
- Move all RichTextBox manipulation: `AppendMessage()`, `AppendDelta()`, spinner logic (`OnVoiceCommandStarted`, `OnSpinnerTick`, `StopSpinner`, `OnVoiceDelta`, `OnVoiceCommandCompleted`).
- Owns the `RichTextBox` control reference and all color constants.
- Exposes: `AppendMessage(UIMessage)`, `AppendDelta(string)`, `StartThinking()`, `StopThinking()`.

**PttController**:
- Move `PttPress()`, `PttRelease()`, `OnKeyDown`, `OnKeyUp`, `_pttHeld` state.
- Exposes events: `PttStarted`, `PttEnded`.
- `MainForm` wires `PttController.PttStarted` → `RealtimeClient.StartPtt()`.

---

### Phase 3: Consistency & Code Quality

> Standardize patterns, eliminate duplication, improve maintainability.

#### 3.1 — Unify JSON library to `System.Text.Json` · ✅ Done
**Addresses**: 8.3  
**Files**: `RealtimeClient.cs`, `UserSettings.cs`, all files using `Newtonsoft.Json`  
**What**:
- Replace all `Newtonsoft.Json` usage with `System.Text.Json`.
- Remove `JObject.Parse`, `JObject`, `JToken` → use `JsonDocument` or `JsonNode`.
- Remove `JsonConvert.SerializeObject` → use `JsonSerializer.Serialize`.
- Remove the `Newtonsoft.Json` NuGet package from `VoiceAssistant.csproj`.

#### 3.2 — Replace hand-built JSON strings with typed response models · ✅ Done
**Addresses**: 8.2  
**Files**: `CopilotCliTool.cs`, `RealtimeClient.cs`  
**What**:
- Define record types for tool responses:
  ```csharp
  record ToolSuccess(bool Success, string Method, string Message);
  record ToolError(string Error);
  ```
- Replace `$"{{ \"success\": true, ... }}"` with `JsonSerializer.Serialize(new ToolSuccess(...))`.
- This prevents JSON syntax errors from string interpolation and enables refactoring.

#### 3.3 — Extract magic numbers to named constants · ✅ Done
**Addresses**: 8.1  
**Files**: `RealtimeClient.cs`, `EmbeddedTerminal.cs`, `CopilotCliTool.cs`  
**What**:
- Define constants:
  ```csharp
  const int AudioSampleRate = 24000;
  const int AudioBitsPerSample = 16;
  const int AudioChannels = 1;
  const int BytesPerMs = 48; // 24000 * 2 / 1000
  const int WebSocketBufferSize = 65536;
  const int KeystrokeDelayMs = 5;
  const int FocusSettleMs = 100;
  const int EchoGuardCushionMs = 500;
  const int ConnectTimeoutSeconds = 60;
  const int WindowDiscoveryTimeoutMs = 15000;
  ```

#### 3.4 — Consistent UI thread marshalling pattern · ✅ Done
**Addresses**: 5.1  
**Files**: `MainForm.cs`  
**What**:
- Create a helper method:
  ```csharp
  private void RunOnUI(Action action)
  {
      if (InvokeRequired) Invoke(action);
      else action();
  }
  ```
- Replace all `if (InvokeRequired) { Invoke(() => ...); } else ...;` patterns with `RunOnUI(() => ...)`.
- Replace the delegates in `StartEmbeddedTerminalAsync` that wrap in `async` lambdas with simple `RunOnUI` calls.

#### 3.5 — Remove hardcoded project root fallback · ✅ Done
**Addresses**: 8.4  
**Files**: `CopilotCliTool.cs`  
**What**:
- Remove the `CopilotCLISubFolder` hardcoded fallback path.
- If no project root is found, use `Environment.CurrentDirectory` (already the last resort).

#### 3.6 — Rename classes to reflect their responsibility · ❌ Not Started
**Addresses**: 10.1  
**Files**: Multiple  
**What**: Rename classes whose names are vague, misleading, or too generic. Apply renames consistently across all usages (constructors, `using` aliases, comments, XML docs). File names must be updated to match.

> ⚠️ **Breakage risk for `AppSettings` → `ApplicationDefaults`**: `AppSettings.cs` uses `JsonConvert.DeserializeObject<AppSettings>(...)`. The generic type parameter must be updated to `<ApplicationDefaults>` at the same time as the class rename, or deserialization fails at runtime.

| Current Name | Suggested Name | Reason |
|---|---|---|
| `RealtimeClient` | `AzureRealtimeApiClient` | Makes clear this is specific to the Azure OpenAI Realtime API, not a generic realtime connector. |
| `NarrationServer` | `CliNarrationBridge` | Clarifies it bridges Copilot CLI text narration to spoken audio — not a general server. |
| `McpInstaller` | `McpServerRegistrar` | "Installer" implies software installation; this class registers/unregisters config entries. |
| `AppLog` | `SessionFileLogger` | Describes function (file-based, session-scoped logging), not just a vague "log". |
| `AppSettings` | `ApplicationDefaults` | Distinguishes it from `UserSettings` — these are read-only bundled defaults from `appsettings.json`. |
| `SetupDialog` | `AzureConnectionSetupDialog` | Identifies the dialog's scope: first-run Azure endpoint and auth configuration. |
| `SetupOverlay` | `AzureConnectionOverlay` | Same intent — the inline overlay panel for configuring Azure connectivity. |
| `VoiceOutput` | `WindowsSpeechQueue` | Describes that it's a queue-based TTS wrapper using `System.Speech.Synthesis`. |

#### 3.7 — Rename ambiguous method names · ✅ Done
**Addresses**: 10.2  
**Files**: `RealtimeClient.cs`, `EmbeddedTerminal.cs`  
**What**: Rename methods where the name describes mechanics rather than intent.

> ⚠️ **Breakage risk for `pressEnter` → `submitWithEnter`**: `CopilotCliTool.cs` calls `SendTextAsync` using a named argument (`pressEnter: submit`). The caller must be updated to `submitWithEnter: submit` at the same time, or the build fails.

| File | Current Name | Suggested Name | Reason |
|---|---|---|---|
| `RealtimeClient.cs` | `HandleEvent(string json)` | `DispatchRealtimeServerEvent(string json)` | "Handle" is vague. This method parses JSON and dispatches to per-event-type handlers. |
| `RealtimeClient.cs` | `InitAudio()` | `InitializeAudioCaptureAndPlayback()` | "Init" is an abbreviation. The method sets up both microphone capture and speaker playback. |
| `RealtimeClient.cs` | `ComputeRms(byte[] buf, int length)` | `ComputeAudioRmsVolume(byte[] audioBuffer, int sampleCount)` | `Rms` is a domain acronym (Root Mean Square). Both the method and its parameters need spelling out. |
| `EmbeddedTerminal.cs` | `SendTextAsync(string text, bool pressEnter)` | Rename parameter: `bool submitWithEnter` | The boolean parameter `pressEnter` is ambiguous at the call site (`SendTextAsync("cmd", true)`). `submitWithEnter` makes the intent obvious. |

#### 3.8 — Add `is`/`has` prefix to boolean fields · ✅ Done
**Addresses**: 10.3  
**Files**: `MainForm.cs`, `RealtimeClient.cs`, `VoiceOutput.cs`, `SetupOverlay.cs`  
**What**: Rename boolean fields so they read naturally as yes/no questions.

| File | Current | Renamed |
|---|---|---|
| `MainForm.cs` | `_connected` | `_isConnected` |
| `MainForm.cs` | `_pttHeld` | `_isPttHeld` |
| `MainForm.cs` | `_spinnerActive` | `_isSpinnerActive` |
| `MainForm.cs` | `_dragging` | `_isDragging` |
| `RealtimeClient.cs` | `_connected` | `_isConnected` |
| `RealtimeClient.cs` | `_pttActive` | `_isPttActive` |
| `RealtimeClient.cs` | `_modelSpeaking` | `_isModelSpeaking` |
| `RealtimeClient.cs` | `_responseInProgress` | `_isResponseInProgress` |
| `VoiceOutput.cs` | `_suppressed` | `_isSuppressed` |
| `SetupOverlay.cs` | `_authenticated` | `_isAuthenticated` |

#### 3.9 — Spell out abbreviated field names · ✅ Done
**Addresses**: 10.4  
**Files**: `MainForm.cs`, `RealtimeClient.cs`, `NarrationServer.cs`, `VoiceOutput.cs`  
**What**: Replace abbreviated field names with descriptive ones. Apply consistently across all references.

| File | Current | Renamed | Expanded meaning |
|---|---|---|---|
| `RealtimeClient.cs` | `_ws` | `_webSocket` | WebSocket connection |
| `RealtimeClient.cs`, `NarrationServer.cs`, `VoiceOutput.cs` | `_cts` | `_cancellationTokenSource` | CancellationTokenSource |
| `RealtimeClient.cs` | `_fmt` | `_audioWaveFormat` | NAudio WaveFormat (24 kHz, 16-bit, mono) |
| `MainForm.cs` | `_tts` | `_voiceOutputQueue` | VoiceOutput (queued TTS) |
| `RealtimeClient.cs` | `_waveIn` | `_microphoneCapture` | WaveInEvent for microphone recording |
| `RealtimeClient.cs` | `_waveOut` | `_speakerPlayback` | WaveOutEvent for speaker output |
| `RealtimeClient.cs` | `_playback` | `_audioPlaybackBuffer` | BufferedWaveProvider feeding the speaker |
| `NarrationServer.cs` | `_listener` | `_narrationHttpListener` | HttpListener accepting WebSocket connections |

#### 3.10 — Rename abbreviated locals and parameters · ✅ Done
**Addresses**: 10.5  
**Files**: `RealtimeClient.cs`, `CopilotCliTool.cs`, `NarrationServer.cs`  
**What**: Rename cryptic local variables and parameters inside method bodies. Prioritize the ones that appear in the longest or most complex methods.

| File | Method | Current | Renamed |
|---|---|---|---|
| `RealtimeClient.cs` | `ReceiveLoopAsync` | `buf` | `receiveBuffer` |
| `RealtimeClient.cs` | `ReceiveLoopAsync` | `sb` | `messageBuilder` |
| `RealtimeClient.cs` | `HandleEvent` | `ev` | `serverEvent` |
| `RealtimeClient.cs` | `HandleEvent` | `b64` | `base64AudioData` |
| `RealtimeClient.cs` | `HandleEvent` | `dcid` | `callId` |
| `RealtimeClient.cs` | `HandleEvent` | `errObj` / `errType` | `errorData` / `errorType` |
| `RealtimeClient.cs` | `BuildUriAsync` | `ep` | `endpoint` |
| `RealtimeClient.cs` | `BuildUriAsync` | `dep` | `deploymentName` |
| `RealtimeClient.cs` | `ComputeRms` | `s` | `sample` |
| `CopilotCliTool.cs` | `RunPromptModeAsync` | `sb` | `responseBuilder` |
| `CopilotCliTool.cs` | `RunPromptModeAsync` | `psi` | `processStartInfo` |
| `CopilotCliTool.cs` | `RunPromptModeAsync` | `proc` | `copilotProcess` |
| `CopilotCliTool.cs` | `RunPromptModeAsync` | `sid` | `sessionId` |
| `CopilotCliTool.cs` | `FindProjectRoot` | `dir` | `searchDirectory` |
| `NarrationServer.cs` | `AcceptLoopAsync` | `ctx` | `httpContext` |
| `NarrationServer.cs` | `HandleWebSocketAsync` | `wsCtx` | `webSocketContext` |
| `NarrationServer.cs` | `HandleWebSocketAsync` | `ws` | `webSocket` |
| `NarrationServer.cs` | `HandleWebSocketAsync` | `buf` | `receiveBuffer` |
| `NarrationServer.cs` | `HandleWebSocketAsync` | `ack` | `acknowledgementJson` |

---

### Phase 4: Resilience & Observability

> Improve production reliability and debugging experience.

#### 4.1 — Add automatic WebSocket reconnection with exponential backoff · ✅ Done
**Addresses**: 4.1  
**Files**: `RealtimeClient.cs` (or new `ConnectionManager.cs`)  
**What**:
- When the receive loop exits due to a non-user-initiated disconnect, attempt reconnection.
- Backoff schedule: 1s → 2s → 4s → 8s → 16s → 30s (cap).
- Max retries: 5 (then surface error to user and stop).
- Fire `StatusChanged("Reconnecting…", ClientState.Connecting)` during retries.
- Reset backoff on successful connection.

#### 4.2 — Improve log rotation · ✅ Done
**Addresses**: 7.2  
**Files**: `AppLog.cs`  
**What**:
- Instead of deleting the log at 200 KB, keep the last N sessions (e.g., rename to `app.log.1` and start fresh).
- Or truncate to the last 100 KB when the file exceeds 200 KB.

#### 4.3 — Redact user speech from file logs · ✅ Done
**Addresses**: 7.3  
**Files**: `RealtimeClient.cs`  
**What**:
- In the STT transcription log line, truncate to first 20 characters + "…" or hash the content.
- Keep full transcripts in a debug-only mode controlled by `appsettings.json`.

#### 4.4 — Validate settings on load, not just in dialog · ✅ Done
**Addresses**: 6.3  
**Files**: `UserSettings.cs`, `Program.cs`  
**What**:
- Add a `Validate()` method to `UserSettings` that checks endpoint format and deployment name.
- Call it in `Program.Main` after `Load()`. If invalid, reset `IsConfigured = false` so the setup dialog appears.

---

### Phase 5: Future Improvements (Nice to Have)

> These are lower priority but improve long-term maintainability.

#### 5.1 — Introduce interfaces for testability · ❌ Not Started
**Addresses**: 2.2  
**Files**: New interfaces + adapters  
**What**:
- `IAudioCapture` / `IAudioPlayback` — wrapping NAudio.
- `ICredentialStore` — wrapping PasswordVault.
- `IWebSocketClient` — wrapping ClientWebSocket.
- `ITokenProvider` — wrapping TokenProvider.
- This enables unit testing of `RealtimeClient`, tool logic, and token refresh without real hardware or Azure.

#### 5.2 — Add a test project · ❌ Not Started
**Addresses**: 2.3  
**Files**: New `VoiceAssistant.Tests/` project  
**What**:
- Test `UserSettings.NormalizeEndpoint` and `IsValidEndpoint` (pure logic).
- Test `UserMemory` CRUD operations (with temp file).
- Test `ToolRegistry` dispatch (with mock implementations).
- Test `UIMessageBus` event delivery.
- Test `RealtimeEventRouter` JSON parsing against sample server messages.

#### 5.3 — Convert `UIMessageBus` to instance-based · ❌ Not Started
**Addresses**: 1.3  
**Files**: `UIMessageBus.cs`, all consumers  
**What**:
- Make `UIMessageBus` a regular class (not static).
- Create single instance in `Program.Main` and inject everywhere.
- Enables scoped lifetime and independent testing.

#### 5.4 — Move configuration to a unified model · ❌ Not Started
**Addresses**: 6.2  
**Files**: `AppSettings.cs`, `UserSettings.cs`, new `VoiceAssistantConfig.cs`  
**What**:
- Merge `AppSettings` and `UserSettings` into a single `VoiceAssistantConfig` with sections.
- At startup, load both sources and merge into one config object.
- All consumers receive `VoiceAssistantConfig` instead of reaching into two different objects.

---

## Summary: Effort Estimates by Phase

| Phase | Focus | Files Changed | New Files | Risk | Status |
|-------|-------|---------------|-----------|------|--------|
| **1** | Critical Fixes | 4 | 0 | Low — bug fixes only | ✅ 4/4 done |
| **2** | Structural Refactoring | 5 | 4–5 | Medium — changes class boundaries | ✅ 5/5 done |
| **3** | Code Quality & Naming | 8–10 | 0 | Low — mechanical refactoring | ✅ 9/10 done (3.6 deferred) |
| **4** | Resilience | 3–4 | 0–1 | Low — additive features | ✅ 4/4 done |
| **5** | Future / Testability | 8+ | 6+ | Medium — large surface area | ❌ 0/4 |

### Recommended Execution Order

```
Phase 1 (Critical Fixes)
    ↓
Phase 3 (Code Quality) — easier, builds confidence
    ↓
Phase 2 (Structural Refactoring) — core architecture changes
    ↓
Phase 4 (Resilience) — benefits from cleaner structure
    ↓
Phase 5 (Testability) — benefits from all prior work
```

Phase 3 is recommended before Phase 2 because it standardizes patterns (JSON library, constants, marshalling) that would otherwise need to be done twice — once in the old code and once in the extracted classes.
