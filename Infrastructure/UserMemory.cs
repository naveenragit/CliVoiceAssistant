using System.Text.Json;

namespace VoiceAssistant.Infrastructure;

/// <summary>
/// Persistent key-value memory store shared between Copilot CLI sessions and
/// the voice assistant.  Stored as a JSON file at ~/.copilot/user-memory.json.
///
/// Both the Copilot CLI (via session_store / copilot-instructions) and the
/// voice assistant (injected into the Realtime API system prompt) can read
/// these facts.  The voice assistant can also write new facts at runtime via
/// the "remember" tool.
/// </summary>
public static class UserMemory
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "user-memory.json");

    private static readonly object _lock = new();

    /// <summary>All stored facts, keyed by a short label.</summary>
    public static Dictionary<string, string> GetAll()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Store or update a single fact.</summary>
    public static void Set(string key, string value)
    {
        lock (_lock)
        {
            var facts = GetAll();
            facts[key] = value;
            Save(facts);
        }
    }

    /// <summary>Remove a fact by key.</summary>
    public static bool Remove(string key)
    {
        lock (_lock)
        {
            var facts = GetAll();
            if (!facts.Remove(key)) return false;
            Save(facts);
            return true;
        }
    }

    /// <summary>
    /// Returns a block of text suitable for injecting into a system prompt.
    /// Returns empty string if no facts are stored.
    /// </summary>
    public static string ToSystemPromptBlock()
    {
        var facts = GetAll();
        if (facts.Count == 0) return "";

        var lines = new List<string> { "\n\n--- User Memory (persistent facts about this user) ---" };
        foreach (var kv in facts)
            lines.Add($"• {kv.Key}: {kv.Value}");
        lines.Add("--- End User Memory ---");
        return string.Join("\n", lines);
    }

    private static void Save(Dictionary<string, string> facts)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(facts, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(FilePath, json);
        AppLog.Info($"UserMemory: saved {facts.Count} facts to {FilePath}");
    }
}
