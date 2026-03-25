using System.Speech.Synthesis;
using System.Threading.Channels;

namespace VoiceAssistant;

/// <summary>
/// Queue-based TTS using the Windows built-in speech engine
/// (System.Speech.Synthesis.SpeechSynthesizer — no API key required).
///
/// Used for System / Tool messages pushed to <see cref="UIMessageBus"/> that
/// are NOT part of the Azure Realtime voice conversation.
///
/// Call <see cref="SuppressAsync"/> while the Azure Realtime model is speaking
/// so the two audio streams don't overlap.
/// </summary>
public sealed class VoiceOutput : IAsyncDisposable
{
    private readonly SpeechSynthesizer              _synth;
    private readonly Channel<string>                _queue;
    private readonly CancellationTokenSource        _cts = new();
    private volatile bool                           _suppressed;

    public VoiceOutput()
    {
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
        _synth.Rate   = 1;    // –10 (slowest) … +10 (fastest)
        _synth.Volume = 90;

        _queue = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        _ = Task.Run(DrainLoopAsync, _cts.Token);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues text for speech.  Returns immediately; speaking happens in
    /// background.  Safe to call from any thread.
    /// </summary>
    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _queue.Writer.TryWrite(text);
    }

    /// <summary>
    /// While <paramref name="suppress"/> is <c>true</c>, queued items are
    /// silently discarded so they don't collide with model audio output.
    /// </summary>
    public void SetSuppressed(bool suppress) => _suppressed = suppress;

    /// <summary>Interrupts current speech and clears the queue.</summary>
    public void Clear()
    {
        _synth.SpeakAsyncCancelAll();
        while (_queue.Reader.TryRead(out _)) { }
    }

    // ── Background drain loop ─────────────────────────────────────────────────

    private async Task DrainLoopAsync()
    {
        await foreach (var text in _queue.Reader.ReadAllAsync(_cts.Token))
        {
            if (_suppressed) continue;                  // skip while model is speaking
            if (_cts.Token.IsCancellationRequested) break;

            await Task.Run(() =>
            {
                try { _synth.Speak(text); }             // blocking synchronous call
                catch (OperationCanceledException) { }
                catch { /* audio device errors — ignore */ }
            }, _cts.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.Complete();
        _cts.Cancel();
        _synth.SpeakAsyncCancelAll();
        await Task.Delay(100);      // let the drain loop exit cleanly
        _synth.Dispose();
        _cts.Dispose();
    }
}
