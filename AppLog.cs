namespace VoiceAssistant;

/// <summary>
/// Simple append-only log file at %APPDATA%\VoiceAssistant\app.log.
/// Also pushes WARNING/ERROR lines to UIMessageBus so they appear in the chat.
/// Call AppLog.Write() from anywhere — thread-safe.
/// </summary>
public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceAssistant", "app.log");

    private static readonly object _lock = new();

    static AppLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Rotate: keep last 200 KB
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200_000)
                File.Delete(LogPath);
            File.AppendAllText(LogPath,
                $"\n──── Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ────\n");
        }
        catch { /* never crash on logging */ }
    }

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg, toChat: true);
    // Error: always written to file. Set toChat=true only for short UI-safe messages.
    public static void Error(string msg) => Write("ERROR", msg, toChat: false);

    public static void Write(string level, string msg, bool toChat = false)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, line + "\n"); }
            catch { /* ignore disk errors */ }
        }

        if (toChat)
            UIMessageBus.PushSystem(msg, readAloud: level == "ERROR");
    }

    public static string LogFilePath => LogPath;
}
