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

---

## Part 2: Refactoring Plan

Based on the best practice violations identified above, here is the prioritized refactoring plan organized by phase.

---

### Phase 1: Critical Fixes (Safety & Resource Leaks)

> Fix bugs and resource leaks that affect stability in production.

#### 1.1 — Fix resource disposal chain
**Addresses**: 3.1  
**Files**: `MainForm.cs`, `RealtimeClient.cs`  
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

#### 1.2 — Fix ToolTip handle leak
**Addresses**: 3.4  
**Files**: `MainForm.cs`  
**What**:
- Create a single `ToolTip` instance as a field in `MainForm`.
- Reuse it in `SetMicState()` and `BuildUI()` instead of allocating new ones.
- Dispose it in `OnFormClosing`.

#### 1.3 — Add timeout to prompt-mode process
**Addresses**: 4.4  
**Files**: `CopilotCliTool.cs`  
**What**:
- Add a 30-second timeout to `RunPromptModeAsync` using `proc.WaitForExitAsync(ct)` with a CancellationTokenSource timeout.
- Kill the process if it exceeds the timeout.

#### 1.4 — Use `Interlocked` for `_pttAudioBytes`
**Addresses**: 5.2  
**Files**: `RealtimeClient.cs`  
**What**:
- Replace `_pttAudioBytes += e.BytesRecorded` with `Interlocked.Add(ref _pttAudioBytes, e.BytesRecorded)`.
- Replace reads with `Interlocked.CompareExchange(ref _pttAudioBytes, 0, 0)` or `Volatile.Read`.

---

### Phase 2: Structural Refactoring (Separation of Concerns)

> Break apart monolithic classes into focused, testable components.

#### 2.1 — Extract `AudioManager` from `RealtimeClient`
**Addresses**: 1.4  
**Files**: New `AudioManager.cs`, modify `RealtimeClient.cs`  
**What**:
- Move `InitAudio()`, `OnMicData()`, `ComputeRms()`, `_waveIn`, `_waveOut`, `_playback`, `_fmt`, and `GetPlaybackTailMs()` into a new `AudioManager` class.
- Expose methods: `StartCapture()`, `StopCapture()`, `PlayAudio(byte[])`, `GetBufferedTailMs()`.
- Expose events: `AudioCaptured(byte[] pcm)`, `VolumeChanged(float rms)`.
- `RealtimeClient` owns an `AudioManager` instance and wires events.
- `AudioManager` implements `IDisposable`.

#### 2.2 — Extract `ToolRegistry` / `ToolDispatcher`
**Addresses**: 1.4, 1.5  
**Files**: New `ToolRegistry.cs`, modify `RealtimeClient.cs`, `CopilotCliTool.cs`  
**What**:
- Create a `ToolRegistry` that holds tool definitions and dispatch logic.
- Move `RememberToolDefinition`, `ExecuteRememberTool()`, `ExecuteToolAndRespondAsync()`, and `ExecuteCopilotCliToolAsync()` out of `RealtimeClient`.
- `ToolRegistry` exposes: `IReadOnlyList<object> Definitions`, `Task<string> ExecuteAsync(string name, string argsJson)`.
- `RealtimeClient` delegates `response.function_call_arguments.done` to `ToolRegistry`.

#### 2.3 — Extract `WebSocketEventRouter` from `RealtimeClient.HandleEvent`
**Addresses**: 1.4  
**Files**: New `RealtimeEventRouter.cs`, modify `RealtimeClient.cs`  
**What**:
- The `HandleEvent(string json)` method is a 200+ line switch statement. Extract it into a `RealtimeEventRouter` class.
- Each case becomes a handler method.
- The router raises domain events (e.g., `AudioDeltaReceived`, `TranscriptCompleted`, `ToolCallRequested`, `ErrorReceived`).
- `RealtimeClient` subscribes to these events instead of inline processing.

#### 2.4 — Convert `CopilotCliTool` from static to instance class
**Addresses**: 1.2, 2.1  
**Files**: `CopilotCliTool.cs`, `MainForm.cs`, `Program.cs`  
**What**:
- Make `CopilotCliTool` a regular class with constructor injection for `EmbeddedTerminal`.
- Replace static properties with constructor parameters and instance fields.
- Remove the static delegate properties (`OnDelta`, `OnCommandStarted`, `OnCommandCompleted`); use events instead.
- Create the instance in `Program.cs` and inject it into `MainForm` and `RealtimeClient`.

#### 2.5 — Slim down `MainForm` — Extract `ChatRenderer` and `PttController`
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

#### 3.1 — Unify JSON library to `System.Text.Json`
**Addresses**: 8.3  
**Files**: `RealtimeClient.cs`, `UserSettings.cs`, all files using `Newtonsoft.Json`  
**What**:
- Replace all `Newtonsoft.Json` usage with `System.Text.Json`.
- Remove `JObject.Parse`, `JObject`, `JToken` → use `JsonDocument` or `JsonNode`.
- Remove `JsonConvert.SerializeObject` → use `JsonSerializer.Serialize`.
- Remove the `Newtonsoft.Json` NuGet package from `VoiceAssistant.csproj`.

#### 3.2 — Replace hand-built JSON strings with typed response models
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

#### 3.3 — Extract magic numbers to named constants
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

#### 3.4 — Consistent UI thread marshalling pattern
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

#### 3.5 — Remove hardcoded project root fallback
**Addresses**: 8.4  
**Files**: `CopilotCliTool.cs`  
**What**:
- Remove the `CopilotCLISubFolder` hardcoded fallback path.
- If no project root is found, use `Environment.CurrentDirectory` (already the last resort).

---

### Phase 4: Resilience & Observability

> Improve production reliability and debugging experience.

#### 4.1 — Add automatic WebSocket reconnection with exponential backoff
**Addresses**: 4.1  
**Files**: `RealtimeClient.cs` (or new `ConnectionManager.cs`)  
**What**:
- When the receive loop exits due to a non-user-initiated disconnect, attempt reconnection.
- Backoff schedule: 1s → 2s → 4s → 8s → 16s → 30s (cap).
- Max retries: 5 (then surface error to user and stop).
- Fire `StatusChanged("Reconnecting…", ClientState.Connecting)` during retries.
- Reset backoff on successful connection.

#### 4.2 — Improve log rotation
**Addresses**: 7.2  
**Files**: `AppLog.cs`  
**What**:
- Instead of deleting the log at 200 KB, keep the last N sessions (e.g., rename to `app.log.1` and start fresh).
- Or truncate to the last 100 KB when the file exceeds 200 KB.

#### 4.3 — Redact user speech from file logs
**Addresses**: 7.3  
**Files**: `RealtimeClient.cs`  
**What**:
- In the STT transcription log line, truncate to first 20 characters + "…" or hash the content.
- Keep full transcripts in a debug-only mode controlled by `appsettings.json`.

#### 4.4 — Validate settings on load, not just in dialog
**Addresses**: 6.3  
**Files**: `UserSettings.cs`, `Program.cs`  
**What**:
- Add a `Validate()` method to `UserSettings` that checks endpoint format and deployment name.
- Call it in `Program.Main` after `Load()`. If invalid, reset `IsConfigured = false` so the setup dialog appears.

---

### Phase 5: Future Improvements (Nice to Have)

> These are lower priority but improve long-term maintainability.

#### 5.1 — Introduce interfaces for testability
**Addresses**: 2.2  
**Files**: New interfaces + adapters  
**What**:
- `IAudioCapture` / `IAudioPlayback` — wrapping NAudio.
- `ICredentialStore` — wrapping PasswordVault.
- `IWebSocketClient` — wrapping ClientWebSocket.
- `ITokenProvider` — wrapping TokenProvider.
- This enables unit testing of `RealtimeClient`, tool logic, and token refresh without real hardware or Azure.

#### 5.2 — Add a test project
**Addresses**: 2.3  
**Files**: New `VoiceAssistant.Tests/` project  
**What**:
- Test `UserSettings.NormalizeEndpoint` and `IsValidEndpoint` (pure logic).
- Test `UserMemory` CRUD operations (with temp file).
- Test `ToolRegistry` dispatch (with mock implementations).
- Test `UIMessageBus` event delivery.
- Test `RealtimeEventRouter` JSON parsing against sample server messages.

#### 5.3 — Convert `UIMessageBus` to instance-based
**Addresses**: 1.3  
**Files**: `UIMessageBus.cs`, all consumers  
**What**:
- Make `UIMessageBus` a regular class (not static).
- Create single instance in `Program.Main` and inject everywhere.
- Enables scoped lifetime and independent testing.

#### 5.4 — Move configuration to a unified model
**Addresses**: 6.2  
**Files**: `AppSettings.cs`, `UserSettings.cs`, new `VoiceAssistantConfig.cs`  
**What**:
- Merge `AppSettings` and `UserSettings` into a single `VoiceAssistantConfig` with sections.
- At startup, load both sources and merge into one config object.
- All consumers receive `VoiceAssistantConfig` instead of reaching into two different objects.

---

## Summary: Effort Estimates by Phase

| Phase | Focus | Files Changed | New Files | Risk |
|-------|-------|---------------|-----------|------|
| **1** | Critical Fixes | 4 | 0 | Low — bug fixes only |
| **2** | Structural Refactoring | 5 | 4–5 | Medium — changes class boundaries |
| **3** | Code Quality | 6–8 | 0 | Low — mechanical refactoring |
| **4** | Resilience | 3–4 | 0–1 | Low — additive features |
| **5** | Future / Testability | 8+ | 6+ | Medium — large surface area |

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
