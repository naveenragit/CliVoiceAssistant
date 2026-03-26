using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceAssistant;

/// <summary>
/// On startup, registers the Voice Assistant's MCP server (narrate + memory tools)
/// into the user-level Copilot CLI config at ~/.copilot/mcp-config.json.
/// This ensures the embedded CLI picks up the narrate tool without needing
/// a separate plugin install.
/// </summary>
public static class McpInstaller
{
    private const string ServerName = "voice-assistant";

    private static readonly string McpConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "mcp-config.json");

    /// <summary>
    /// Ensures the voice-assistant MCP server is registered in the user-level config.
    /// Uses absolute path to memory-server.js so it works from any working directory.
    /// </summary>
    public static void EnsureRegistered()
    {
        try
        {
            var serverJsPath = FindMemoryServerJs();
            if (serverJsPath == null)
            {
                AppLog.Warn("McpInstaller: could not find mcp-server/memory-server.js — skipping MCP registration");
                return;
            }

            // Load existing config or create new
            JsonObject root;
            if (File.Exists(McpConfigPath))
            {
                var existing = File.ReadAllText(McpConfigPath);
                root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            // Ensure mcpServers key exists
            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            // Add/update our server entry with absolute path
            var serverConfig = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = "node",
                ["args"] = new JsonArray(serverJsPath),
            };

            servers[ServerName] = serverConfig;

            // Write back
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = root.ToJsonString(options);
            Directory.CreateDirectory(Path.GetDirectoryName(McpConfigPath)!);
            File.WriteAllText(McpConfigPath, json);

            AppLog.Info($"McpInstaller: registered '{ServerName}' MCP server → {serverJsPath}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"McpInstaller: failed to register MCP server: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the voice-assistant entry from mcp-config.json.
    /// Called on app exit to keep things clean (optional).
    /// </summary>
    public static void Unregister()
    {
        try
        {
            if (!File.Exists(McpConfigPath)) return;

            var existing = File.ReadAllText(McpConfigPath);
            var root = JsonNode.Parse(existing)?.AsObject();
            if (root?["mcpServers"] is JsonObject servers && servers.ContainsKey(ServerName))
            {
                servers.Remove(ServerName);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(McpConfigPath, root.ToJsonString(options));
                AppLog.Info($"McpInstaller: unregistered '{ServerName}' MCP server");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"McpInstaller: cleanup failed: {ex.Message}");
        }
    }

    /// <summary>Find memory-server.js by walking up from the exe directory.</summary>
    private static string? FindMemoryServerJs()
    {
        // Check relative to exe
        var baseDir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(baseDir, "mcp-server", "memory-server.js");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
            var parent = Path.GetDirectoryName(baseDir);
            if (parent == null || parent == baseDir) break;
            baseDir = parent;
        }

        // Check CWD
        var cwdCandidate = Path.Combine(Environment.CurrentDirectory, "mcp-server", "memory-server.js");
        if (File.Exists(cwdCandidate))
            return Path.GetFullPath(cwdCandidate);

        return null;
    }
}
