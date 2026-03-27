using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Tools;

/// <summary>
/// Sends commands to the Copilot CLI via keystroke injection into the embedded terminal.
/// Falls back to prompt mode if the terminal is not available.
///
/// V4 Architecture: voice commands are typed directly into the embedded conhost terminal
/// so the user can see both the command and Copilot's response on screen.
/// Prompt mode (-p) is kept as a fallback.
/// </summary>

// Tool response models for CopilotCliTool
file record CliSuccessResponse(bool success, string method, string message);
file record CliPromptResponse(bool success, int chars, string method, string response);
file record CliErrorResponse(string error);

public sealed class CopilotCliTool
{
    /// <summary>
    /// Text appended to every injected message. Set from appsettings.json copilot.instructionSuffix.
    /// </summary>
    public string InstructionSuffix { get; set; } = "";

    /// <summary>
    /// When true (default), auto-submit commands.
    /// </summary>
    public bool AutoSubmit { get; set; } = true;

    /// <summary>Path to copilot.exe. Auto-detected if not set.</summary>
    public string? CopilotExePath { get; set; }

    /// <summary>
    /// Reference to the embedded terminal for keystroke injection.
    /// When set, voice commands are typed directly into the terminal instead of using prompt mode.
    /// </summary>
    public EmbeddedTerminal? Terminal { get; set; }

    /// <summary>Session ID from the last prompt-mode invocation. Used with --resume for context.</summary>
    private string? _sessionId;

    /// <summary>
    /// Set from --session= command-line arg to resume a prior CLI session.
    /// </summary>
    public string? ResumeSessionId
    {
        get => _sessionId;
        set => _sessionId = value;
    }

    /// <summary>
    /// Delegate fired for each response delta chunk — MainForm wires this
    /// to stream live text into the chat panel.
    /// </summary>
    public event Func<string, Task>? OnDelta;

    /// <summary>
    /// Delegate fired when a voice command starts — MainForm shows the command
    /// in the chat panel and starts the thinking spinner.
    /// </summary>
    public event Func<string, Task>? OnCommandStarted;

    /// <summary>
    /// Delegate fired when a voice command completes — MainForm stops the
    /// thinking spinner.
    /// </summary>
    public event Func<string, Task>? OnCommandCompleted;

    /// <summary>
    /// Project root directory — walks up from the exe to find a dir containing
    /// .github/copilot-instructions.md. Falls back to the exe directory.
    /// </summary>
    private static readonly string ProjectRootDir = FindProjectRoot();

    /// <summary>Returns the discovered project root directory.</summary>
    public static string GetProjectRoot() => ProjectRootDir;

    private static string FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.GetFullPath(dir);
            if (File.Exists(Path.Combine(candidate, ".github", "copilot-instructions.md")))
                return candidate;
            var parent = Path.GetDirectoryName(candidate);
            if (parent == null || parent == candidate) break;
            dir = parent;
        }
        var cwd = Environment.CurrentDirectory;
        if (File.Exists(Path.Combine(cwd, ".github", "copilot-instructions.md")))
            return cwd;
        return cwd;
    }

    // ── Tool definition for Azure OpenAI Realtime API ────────────────────────

    public const string FunctionName = "send_to_copilot_cli";

    public static object ToolDefinition => new
    {
        type        = "function",
        name        = FunctionName,
        description = "Sends a text message or command prompt to the GitHub Copilot CLI " +
                      "terminal session so it appears directly in the input box, ready to " +
                      "submit. Use this when the user asks you to tell Copilot something, " +
                      "run a Copilot command, or pass a task to the CLI.",
        parameters  = new
        {
            type       = "object",
            properties = new
            {
                message = new
                {
                    type        = "string",
                    description = "The full text to inject into the Copilot CLI input prompt.",
                },
                submit = new
                {
                    type        = "boolean",
                    description = "If true (default), press Enter after typing. Set false to leave text for user to review.",
                },
            },
            required = new[] { "message" },
        },
    };

    /// <summary>
    /// Send a command to Copilot CLI by typing it into the embedded terminal.
    /// Falls back to prompt mode (-p flag) if no terminal is available.
    /// </summary>
    public async Task<string> SendAsync(string message, bool submit = true)
    {
        if (string.IsNullOrWhiteSpace(message))
            return JsonSerializer.Serialize(new CliErrorResponse("empty message"));

        var fullMessage = string.IsNullOrEmpty(InstructionSuffix)
            ? message
            : message + InstructionSuffix;

        UIMessageBus.PushTool($"\u2192 Copilot CLI: {message}", readAloud: false);

        // Route through embedded terminal (keystroke injection)
        if (Terminal is { IsRunning: true })
        {
            try
            {
                AppLog.Info($"CopilotCliTool: injecting into terminal: \"{fullMessage[..Math.Min(80, fullMessage.Length)]}...\"");

                if (OnCommandStarted != null)
                {
                    try { await OnCommandStarted(fullMessage); }
                    catch { }
                }

                await Terminal.SendTextAsync(fullMessage, submitWithEnter: submit);

                if (OnCommandCompleted != null)
                {
                    try { await OnCommandCompleted("Command sent to terminal"); }
                    catch { }
                }

                return JsonSerializer.Serialize(new CliSuccessResponse(true, "terminal_keystroke", "Command typed into Copilot CLI terminal. The user can see the response on screen."));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"CopilotCliTool: keystroke injection failed ({ex.Message}), falling back to prompt mode");
                // Fall through to prompt mode
            }
        }

        // Fallback: prompt mode
        try
        {
            var copilotPath = FindCopilotExe();
            AppLog.Info($"CopilotCliTool: running prompt mode: copilot -p \"{fullMessage[..Math.Min(80, fullMessage.Length)]}...\"");

            if (OnCommandStarted != null)
            {
                try { await OnCommandStarted(fullMessage); }
                catch { }
            }

            var response = await RunPromptModeAsync(copilotPath, fullMessage);

            if (OnCommandCompleted != null)
            {
                try { await OnCommandCompleted(response ?? ""); }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                AppLog.Info($"CopilotCliTool: prompt mode response ({response.Length} chars)");
                return JsonSerializer.Serialize(new CliPromptResponse(true, response.Length, "prompt_mode", response));
            }
            else
            {
                AppLog.Warn("CopilotCliTool: prompt mode returned empty response");
                return JsonSerializer.Serialize(new CliErrorResponse("empty response from prompt mode"));
            }
        }
        catch (Exception ex)
        {
            var err = $"Prompt mode failed: {ex.Message}";
            AppLog.Error($"CopilotCliTool: {err}");
            return JsonSerializer.Serialize(new CliErrorResponse(err));
        }
    }

    /// <summary>
    /// Run copilot -p "prompt" --output-format json and accumulate the response.
    /// Deltas are streamed live to the chat panel via OnDelta delegate.
    /// </summary>
    private async Task<string> RunPromptModeAsync(string copilotPath, string prompt)
    {
        var responseBuilder = new StringBuilder();
        var lastMessageContent = "";

        var args = _sessionId != null
            ? $"--resume={_sessionId} -p {EscapeArg(prompt)} --output-format json"
            : $"-p {EscapeArg(prompt)} --output-format json";

        AppLog.Info($"CopilotCliTool: args: {args[..Math.Min(120, args.Length)]}...");

        var processStartInfo = new ProcessStartInfo
        {
            FileName = copilotPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ProjectRootDir,
        };

        using var copilotProcess = new Process { StartInfo = processStartInfo };
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        copilotProcess.Start();

        var stderrTask = copilotProcess.StandardError.ReadToEndAsync();

        try
        {
            while (true)
            {
                var lineTask = copilotProcess.StandardOutput.ReadLineAsync();
                var completedTask = await Task.WhenAny(lineTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
                if (completedTask != lineTask)
                {
                    AppLog.Warn("CopilotCliTool: prompt-mode read timed out after 30s — killing process");
                    break;
                }
                var line = await lineTask;
                if (line == null) break;

            if (!line.StartsWith("{")) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                if (type == "assistant.message_delta")
                {
                    var delta = root.GetProperty("data").GetProperty("deltaContent").GetString();
                    if (delta != null)
                    {
                        responseBuilder.Append(delta);
                        if (OnDelta != null)
                        {
                            try { await OnDelta(delta); }
                            catch { }
                        }
                    }
                }
                else if (type == "assistant.message")
                {
                    var content = root.GetProperty("data").GetProperty("content").GetString();
                    if (!string.IsNullOrWhiteSpace(content))
                        lastMessageContent = content;
                }
                else if (type == "result")
                {
                    if (root.TryGetProperty("sessionId", out var sidElem))
                    {
                        var sessionId = sidElem.GetString();
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            _sessionId = sessionId;
                            AppLog.Info($"CopilotCliTool: session ID = {_sessionId}");
                        }
                    }
                    break;
                }
            }
            catch (JsonException) { }
        }
        }
        catch (OperationCanceledException)
        {
            AppLog.Warn("CopilotCliTool: prompt-mode timed out after 30s");
        }

        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr))
            AppLog.Warn($"CopilotCliTool: stderr: {stderr[..Math.Min(500, stderr.Length)]}");

        if (!copilotProcess.HasExited)
        {
            AppLog.Warn("CopilotCliTool: process still running — killing");
            try { copilotProcess.Kill(true); } catch { }
        }
        else
        {
            AppLog.Info($"CopilotCliTool: process exited with code {copilotProcess.ExitCode}");
            copilotProcess.WaitForExit(5000);
        }

        var result = !string.IsNullOrWhiteSpace(lastMessageContent)
            ? lastMessageContent
            : responseBuilder.ToString();

        return string.IsNullOrWhiteSpace(result) ? "" : result.Trim();
    }

    private static string EscapeArg(string arg)
        => "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private string FindCopilotExe()
    {
        if (!string.IsNullOrEmpty(CopilotExePath) && File.Exists(CopilotExePath))
            return CopilotExePath;

        var wingetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages\GitHub.Copilot_Microsoft.Winget.Source_8wekyb3d8bbwe\copilot.exe");
        if (File.Exists(wingetPath)) return wingetPath;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(';') ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "copilot.exe");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("copilot.exe not found. Install GitHub Copilot CLI via WinGet.");
    }
}
