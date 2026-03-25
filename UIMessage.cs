namespace VoiceAssistant;

// ── Message types ─────────────────────────────────────────────────────────────

public enum MessageRole
{
    User,        // spoken by the local user
    Assistant,   // spoken/returned by the AI model
    System,      // connection status, errors, info
    Tool,        // output from an external tool / function call
}

/// <summary>
/// An immutable message pushed to the UI via <see cref="UIMessageBus"/>.
/// </summary>
/// <param name="Role">Who produced the message.</param>
/// <param name="Text">Display text.</param>
/// <param name="ReadAloud">
///   If <c>true</c>, <see cref="UIMessageBus"/> will also speak the text via
///   <see cref="VoiceOutput"/>. Set <c>false</c> for silent status updates.
/// </param>
public sealed record UIMessage(
    MessageRole Role,
    string      Text,
    bool        ReadAloud  = true,
    DateTime?   Timestamp  = null)
{
    public DateTime At => Timestamp ?? DateTime.Now;
}
