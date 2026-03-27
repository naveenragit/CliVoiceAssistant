using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.UI;

/// <summary>
/// Manages all RichTextBox rendering in the voice chat panel: message display,
/// delta streaming, and the thinking spinner animation.
/// </summary>
public sealed class ChatRenderer : IDisposable
{
    // ── Colors ────────────────────────────────────────────────────────────────
    private static readonly Color TextDefault = Color.FromArgb(226, 232, 240);
    private static readonly Color MutedColor  = Color.FromArgb(100, 116, 139);
    private static readonly Color UserColor   = Color.FromArgb(165, 180, 252);   // lilac
    private static readonly Color AssistColor = Color.FromArgb(134, 239, 172);   // green
    private static readonly Color SystemColor = Color.FromArgb(100, 116, 139);   // grey
    private static readonly Color ToolColor   = Color.FromArgb(251, 191,  36);   // amber

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly RichTextBox _chat;
    private System.Windows.Forms.Timer? _spinnerTimer;
    private int _spinnerFrame;
    private bool _isSpinnerActive;

    public ChatRenderer(RichTextBox chat)
    {
        _chat = chat;
    }

    // ── Message rendering ────────────────────────────────────────────────────

    /// <summary>Append a complete message with timestamp and role prefix.</summary>
    public void AppendMessage(UIMessage msg)
    {
        var (prefix, color) = msg.Role switch
        {
            MessageRole.User      => ("You      ", UserColor),
            MessageRole.Assistant => ("Assistant", AssistColor),
            MessageRole.Tool      => ("Tool     ", ToolColor),
            _                     => ("System   ", SystemColor),
        };

        _chat.SelectionStart  = _chat.TextLength;
        _chat.SelectionLength = 0;

        // Timestamp + role
        _chat.SelectionColor = SystemColor;
        _chat.AppendText($"\n{msg.At:HH:mm}  ");
        _chat.SelectionColor = color;
        _chat.SelectionFont  = new Font(_chat.Font, FontStyle.Regular);
        _chat.AppendText($"{prefix}  ");
        _chat.SelectionColor = TextDefault;
        _chat.AppendText(msg.Text + "\n");

        _chat.SelectionStart = _chat.TextLength;
        _chat.ScrollToCaret();
    }

    /// <summary>Append a delta chunk inline (no newline, no timestamp) — used for streaming.</summary>
    public void AppendDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _chat.SelectionStart  = _chat.TextLength;
        _chat.SelectionLength = 0;
        _chat.SelectionColor  = AssistColor;
        _chat.AppendText(delta);
        _chat.SelectionStart = _chat.TextLength;
        _chat.ScrollToCaret();
    }

    // ── Thinking spinner ─────────────────────────────────────────────────────

    /// <summary>Show the thinking spinner in the chat panel.</summary>
    public void StartThinking()
    {
        StopThinking();

        _spinnerFrame    = 0;
        _isSpinnerActive = true;

        _chat.SelectionStart  = _chat.TextLength;
        _chat.SelectionLength = 0;
        _chat.SelectionColor  = ToolColor;
        _chat.AppendText($"\n  {SpinnerFrames[0]} Thinking…");
        _chat.SelectionStart = _chat.TextLength;
        _chat.ScrollToCaret();

        _spinnerTimer ??= new System.Windows.Forms.Timer { Interval = 80 };
        _spinnerTimer.Tick -= OnSpinnerTick;
        _spinnerTimer.Tick += OnSpinnerTick;
        _spinnerTimer.Start();
    }

    /// <summary>Stop the thinking spinner.</summary>
    public void StopThinking()
    {
        _isSpinnerActive = false;
        _spinnerTimer?.Stop();
    }

    /// <summary>Whether the spinner is currently active.</summary>
    public bool IsThinking => _isSpinnerActive;

    /// <summary>
    /// Handle a voice command delta: stop the spinner on first delta,
    /// replace it with a response header, then append the delta text.
    /// </summary>
    public void HandleVoiceDelta(string delta)
    {
        // First delta — stop spinner, clear spinner line, start response
        if (_isSpinnerActive)
        {
            StopThinking();
            ClearLastLine();

            // Start response header
            _chat.SelectionStart  = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.SelectionColor  = SystemColor;
            _chat.AppendText($"{DateTime.Now:HH:mm}  ");
            _chat.SelectionColor  = AssistColor;
            _chat.AppendText("Copilot   ");
            _chat.SelectionColor  = TextDefault;
        }

        // Append delta text
        if (!string.IsNullOrEmpty(delta))
        {
            _chat.SelectionStart  = _chat.TextLength;
            _chat.SelectionLength = 0;
            _chat.SelectionColor  = TextDefault;
            _chat.AppendText(delta);
            _chat.SelectionStart = _chat.TextLength;
            _chat.ScrollToCaret();
        }
    }

    /// <summary>
    /// Handle voice command completion: if spinner is still active, clear it
    /// and show a "sent" confirmation.
    /// </summary>
    public void HandleVoiceCommandCompleted()
    {
        if (!_isSpinnerActive) return;

        StopThinking();
        ClearLastLine();

        _chat.SelectionStart  = _chat.TextLength;
        _chat.SelectionLength = 0;
        _chat.SelectionColor  = SystemColor;
        _chat.AppendText($"{DateTime.Now:HH:mm}  ");
        _chat.SelectionColor  = ToolColor;
        _chat.AppendText("\u2192 Sent to terminal\n");
        _chat.SelectionStart = _chat.TextLength;
        _chat.ScrollToCaret();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ClearLastLine()
    {
        var text = _chat.Text;
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline >= 0)
        {
            _chat.Select(lastNewline, _chat.TextLength - lastNewline);
            _chat.SelectedText = "\n";
        }
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        if (!_isSpinnerActive) return;
        _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;

        var text = _chat.Text;
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline >= 0)
        {
            var lineStart = lastNewline + 1;
            _chat.Select(lineStart, _chat.TextLength - lineStart);
            _chat.SelectionColor = ToolColor;
            _chat.SelectedText = $"  {SpinnerFrames[_spinnerFrame]} Thinking…";
            _chat.SelectionStart = _chat.TextLength;
        }
    }

    public void Dispose()
    {
        StopThinking();
        _spinnerTimer?.Dispose();
    }
}
