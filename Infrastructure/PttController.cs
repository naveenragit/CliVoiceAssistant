namespace VoiceAssistant.Infrastructure;

/// <summary>
/// Manages push-to-talk (PTT) state — tracks press/release from both the
/// on-screen button and the spacebar, firing events that <see cref="MainForm"/>
/// wires to <see cref="RealtimeClient.StartPtt"/> / <see cref="RealtimeClient.StopPtt"/>.
/// </summary>
public sealed class PttController
{
    private bool _isPttHeld;

    /// <summary>Fired when the user starts a PTT press (button or spacebar).</summary>
    public event Action? PttStarted;

    /// <summary>Fired when the user releases PTT.</summary>
    public event Action? PttEnded;

    /// <summary>Whether PTT is currently held down.</summary>
    public bool IsHeld => _isPttHeld;

    /// <summary>Begin a PTT press. No-op if already held or not connected.</summary>
    public void Press(bool isConnected)
    {
        if (!isConnected || _isPttHeld) return;
        _isPttHeld = true;
        PttStarted?.Invoke();
    }

    /// <summary>End a PTT press. No-op if not currently held.</summary>
    public void Release()
    {
        if (!_isPttHeld) return;
        _isPttHeld = false;
        PttEnded?.Invoke();
    }

    /// <summary>Handle KeyDown for spacebar PTT.</summary>
    public void OnKeyDown(KeyEventArgs e)
    {
        // Only handle spacebar — leave other keys to the form
        if (e.KeyCode != Keys.Space) return;
        e.SuppressKeyPress = true;   // don't type a space into the chat
    }

    /// <summary>Handle KeyUp for spacebar PTT.</summary>
    public void OnKeyUp(KeyEventArgs e)
    {
        // Release is handled separately — this just detects the key
    }
}
