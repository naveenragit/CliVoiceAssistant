namespace VoiceAssistant.Infrastructure;

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

    private const long MaxLogSizeBytes = 200_000;
    private static readonly string BackupLogPath = LogPath + ".1";

    static AppLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Rolling rotation: when log exceeds 200 KB, archive as app.log.1 and start fresh.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogSizeBytes)
            {
                File.Copy(LogPath, BackupLogPath, overwrite: true);
                File.Delete(LogPath);
            }
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
