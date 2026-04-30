using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Tools;

/// <summary>
/// Tool response models — shared across tool implementations.
/// </summary>
public record ToolSuccessResponse(bool success, string method, string message);
public record ToolKeyResponse(bool success, string key, int repeat);
public record ToolRememberResponse(bool success, string key, string value);
public record ToolErrorResponse(string error);

/// <summary>
/// Central registry for all Realtime API tools. Holds tool definitions and
/// dispatches tool calls from the model to the appropriate implementation.
/// </summary>
public sealed class ToolRegistry
{
    private readonly CopilotCliTool _copilotCli;
    private readonly EmbeddedTerminal? _terminal;

    public ToolRegistry(CopilotCliTool copilotCli, EmbeddedTerminal? terminal)
    {
        _copilotCli = copilotCli;
        _terminal   = terminal;
    }

    /// <summary>Update the terminal reference (set after async startup).</summary>
    public EmbeddedTerminal? Terminal
    {
        get => _terminal;
        init => _terminal = value;
    }

    /// <summary>All tool definitions to send in session.update.</summary>
    public object[] Definitions
    {
        get
        {
            var defs = new List<object> { CopilotCliTool.ToolDefinition, SelectOptionToolDefinition, RememberToolDefinition };
            return defs.ToArray();
        }
    }

    /// <summary>
    /// Execute a tool by name and return the JSON result string.
    /// </summary>
    public async Task<string> ExecuteAsync(string name, string argsJson)
    {
        AppLog.Info($"Executing tool '{name}' args={argsJson}");
        UIMessageBus.PushTool($"Calling tool: {name}", readAloud: false);

        try
        {
            return name switch
            {
                CopilotCliTool.FunctionName => await ExecuteCopilotCliAsync(argsJson),
                "select_option"             => await ExecuteSelectOptionAsync(argsJson),
                "remember_fact"             => ExecuteRememberTool(argsJson),
                _                           => JsonSerializer.Serialize(new ToolErrorResponse($"unknown tool '{name}'"))
            };
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tool '{name}' threw: {ex.Message}");
            return JsonSerializer.Serialize(new ToolErrorResponse(ex.Message));
        }
    }

    /// <summary>Whether the tool result should suppress automatic response.create.</summary>
    public static bool IsSilentTool(string name)
        => name is CopilotCliTool.FunctionName or "select_option";

    // ── send_to_copilot_cli ──────────────────────────────────────────────────

    private async Task<string> ExecuteCopilotCliAsync(string argsJson)
    {
        JsonNode? args;
        try { args = JsonNode.Parse(argsJson); }
        catch { return JsonSerializer.Serialize(new ToolErrorResponse("invalid arguments JSON")); }
        if (args == null) return JsonSerializer.Serialize(new ToolErrorResponse("invalid arguments JSON"));

        var message = args["message"]?.GetValue<string>() ?? "";
        var submit  = args["submit"]?.GetValue<bool>() ?? true;
        return await _copilotCli.SendAsync(message, submit);
    }

    // ── select_option ────────────────────────────────────────────────────────

    public static readonly object SelectOptionToolDefinition = new
    {
        type        = "function",
        name        = "select_option",
        description = "Navigate and select options in Copilot CLI selection menus. " +
                      "When Copilot CLI presents a numbered or arrow-key selection list, " +
                      "use this tool to press arrow keys and Enter to choose an option. " +
                      "For example, if the user says 'select the second one' or 'pick option 2', " +
                      "send down arrow presses to reach that option, then press Enter.",
        parameters  = new
        {
            type       = "object",
            properties = new
            {
                key = new
                {
                    type        = "string",
                    description = "The key to send: 'up', 'down', 'enter', 'escape', or 'tab'.",
                    @enum       = new[] { "up", "down", "enter", "escape", "tab" },
                },
                repeat = new
                {
                    type        = "integer",
                    description = "Number of times to press the key. Default 1. " +
                                  "E.g. to select option 3, send 'down' with repeat=2, then call again with 'enter'.",
                },
            },
            required = new[] { "key" },
        },
    };

    private async Task<string> ExecuteSelectOptionAsync(string argsJson)
    {
        JsonNode? args;
        try { args = JsonNode.Parse(argsJson); }
        catch { return JsonSerializer.Serialize(new ToolErrorResponse("invalid arguments JSON")); }
        if (args == null) return JsonSerializer.Serialize(new ToolErrorResponse("invalid arguments JSON"));

        var key    = args["key"]?.GetValue<string>()?.Trim()?.ToLowerInvariant() ?? "";
        var repeat = args["repeat"]?.GetValue<int>() ?? 1;
        if (repeat < 1) repeat = 1;
        if (repeat > 20) repeat = 20;

        if (string.IsNullOrEmpty(key))
            return JsonSerializer.Serialize(new ToolErrorResponse("key is required"));

        var terminal = _copilotCli.Terminal;
        if (terminal is not { IsRunning: true })
            return JsonSerializer.Serialize(new ToolErrorResponse("terminal not available"));

        try
        {
            await terminal.SendKeyAsync(key, repeat);
            return JsonSerializer.Serialize(new ToolKeyResponse(true, key, repeat));
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new ToolErrorResponse($"SendKey failed: {ex.Message}"));
        }
    }

    // ── remember_fact ────────────────────────────────────────────────────────

    public static readonly object RememberToolDefinition = new
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
        JsonNode? args;
        try { args = JsonNode.Parse(argsJson); }
        catch { return JsonSerializer.Serialize(new ToolErrorResponse("invalid arguments JSON")); }
        if (args == null) return JsonSerializer.Serialize(new ToolErrorResponse("invalid arguments JSON"));

        var key   = args["key"]?.GetValue<string>()?.Trim() ?? "";
        var value = args["value"]?.GetValue<string>()?.Trim() ?? "";

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            return JsonSerializer.Serialize(new ToolErrorResponse("key and value are both required"));

        UserMemory.Set(key, value);
        UIMessageBus.PushSystem($"\ud83d\udcdd Remembered: {key} = {value}", readAloud: false);
        return JsonSerializer.Serialize(new ToolRememberResponse(true, key, value));
    }
}
