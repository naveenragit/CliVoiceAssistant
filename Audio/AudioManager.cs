using NAudio.Wave;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Audio;

/// <summary>
/// Manages microphone capture and speaker playback for the Realtime API audio pipeline.
/// Encapsulates NAudio <see cref="WaveInEvent"/> / <see cref="WaveOutEvent"/> lifecycle,
/// RMS volume computation, and playback buffer management.
/// </summary>
public sealed class AudioManager : IDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const int AudioSampleRate    = 24000;
    private const int AudioBitsPerSample = 16;
    private const int AudioChannels      = 1;
    private const int BytesPerMs         = AudioSampleRate * (AudioBitsPerSample / 8) / 1000; // 48
    private const int PlaybackBufferSize = 1024 * 1024;
    private const int AudioLatencyMs     = 80;
    private const int EchoGuardCushionMs = 500;

    private static readonly WaveFormat AudioWaveFormat = new(AudioSampleRate, AudioBitsPerSample, AudioChannels);

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when audio data is captured from the microphone.
    /// The byte array contains raw PCM16 audio data.
    /// </summary>
    public event EventHandler<AudioCapturedEventArgs>? AudioCaptured;

    /// <summary>Fired with the current RMS volume level (0–1) on each mic callback.</summary>
    public event EventHandler<float>? VolumeChanged;

    // ── State ────────────────────────────────────────────────────────────────

    private WaveInEvent?          _microphoneCapture;
    private WaveOutEvent?         _speakerPlayback;
    private BufferedWaveProvider? _audioPlaybackBuffer;

    /// <summary>Whether the audio devices are currently initialized and running.</summary>
    public bool IsInitialized => _microphoneCapture != null;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes microphone capture and speaker playback devices.
    /// Must be called before <see cref="PlayAudio"/> or capture events fire.
    /// </summary>
    public void Initialize()
    {
        // Playback
        _audioPlaybackBuffer = new BufferedWaveProvider(AudioWaveFormat)
        {
            BufferLength            = PlaybackBufferSize,
            DiscardOnBufferOverflow = true,
        };
        _speakerPlayback = new WaveOutEvent { DesiredLatency = AudioLatencyMs };
        _speakerPlayback.Init(_audioPlaybackBuffer);
        _speakerPlayback.Play();

        // Capture
        _microphoneCapture = new WaveInEvent { WaveFormat = AudioWaveFormat, BufferMilliseconds = AudioLatencyMs };
        _microphoneCapture.DataAvailable += OnMicData;
        _microphoneCapture.StartRecording();
    }

    /// <summary>Add PCM16 audio samples to the speaker playback buffer.</summary>
    public void PlayAudio(byte[] audio)
    {
        _audioPlaybackBuffer?.AddSamples(audio, 0, audio.Length);
    }

    /// <summary>Clear all buffered audio from the speaker playback buffer.</summary>
    public void ClearPlaybackBuffer()
    {
        _audioPlaybackBuffer?.ClearBuffer();
    }

    /// <summary>
    /// Estimates how many ms of audio remain in the playback buffer.
    /// Used to delay mic re-enable after the model finishes speaking so the model's
    /// own voice doesn't echo back as new input.
    /// Adds a fixed 500ms cushion for speaker propagation.
    /// </summary>
    public int GetPlaybackTailMs()
    {
        if (_audioPlaybackBuffer == null) return EchoGuardCushionMs;
        var bufferedMs = _audioPlaybackBuffer.BufferedBytes / BytesPerMs;
        var tailMs     = bufferedMs + EchoGuardCushionMs;
        AppLog.Info($"[ECHO-GUARD] playback tail = {bufferedMs}ms buffered + {EchoGuardCushionMs}ms cushion = {tailMs}ms");
        return tailMs;
    }

    /// <summary>Stop microphone capture (e.g., during disconnect).</summary>
    public void StopCapture()
    {
        _microphoneCapture?.StopRecording();
    }

    /// <summary>Stop speaker playback (e.g., during disconnect).</summary>
    public void StopPlayback()
    {
        _speakerPlayback?.Stop();
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        // Compute RMS for the volume meter
        float rms = ComputeAudioRmsVolume(e.Buffer, e.BytesRecorded);
        VolumeChanged?.Invoke(this, rms);

        // Forward raw audio to subscribers
        AudioCaptured?.Invoke(this, new AudioCapturedEventArgs(e.Buffer, e.BytesRecorded));
    }

    private static float ComputeAudioRmsVolume(byte[] audioBuffer, int length)
    {
        double sum = 0;
        int samples = length / 2;
        for (int i = 0; i < length - 1; i += 2)
        {
            short sample = (short)(audioBuffer[i] | (audioBuffer[i + 1] << 8));
            sum += (double)sample * sample;
        }
        return samples > 0 ? (float)Math.Sqrt(sum / samples) / 32768f : 0f;
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _microphoneCapture?.StopRecording();
        _microphoneCapture?.Dispose();
        _speakerPlayback?.Stop();
        _speakerPlayback?.Dispose();
    }
}

/// <summary>Event args carrying raw mic audio data.</summary>
public class AudioCapturedEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    public byte[] Buffer        { get; } = buffer;
    public int    BytesRecorded { get; } = bytesRecorded;
}
