namespace VoiceAssistant;

/// <summary>
/// Thread-safe static pub/sub bus.
///
/// Any component — RealtimeClient, tool runners, background services — can push
/// a message here.  The UI subscribes once and never polls.
///
/// Usage:
///   Push:      UIMessageBus.Push(MessageRole.Assistant, "Hello!");
///   Subscribe: UIMessageBus.MessagePushed += (msg) => ...;
/// </summary>
public static class UIMessageBus
{
    // ── Subscribers ───────────────────────────────────────────────────────────

    /// <summary>Raised on the calling thread; marshal to UI thread inside your handler.</summary>
    public static event Action<UIMessage>? MessagePushed;

    // ── Push API ──────────────────────────────────────────────────────────────

    public static void Push(UIMessage message)
        => MessagePushed?.Invoke(message);

    public static void Push(MessageRole role, string text, bool readAloud = true)
        => Push(new UIMessage(role, text, readAloud));

    // Convenience shorthands
    public static void PushSystem(string text, bool readAloud = false)
        => Push(MessageRole.System, text, readAloud);

    public static void PushTool(string text, bool readAloud = true)
        => Push(MessageRole.Tool, text, readAloud);

    public static void PushAssistant(string text, bool readAloud = true)
        => Push(MessageRole.Assistant, text, readAloud);

    public static void PushUser(string text, bool readAloud = false)
        => Push(MessageRole.User, text, readAloud);
}
