using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace VoiceAssistant;

/// <summary>
/// Local WebSocket server on ws://localhost:9877 that receives narration text
/// from Copilot CLI (via MCP tool) and speaks it aloud through the Voice Assistant.
/// </summary>
public sealed class NarrationServer : IAsyncDisposable
{
    public const int Port = 9877;

    private HttpListener? _listener;
    private CancellationTokenSource _cts = new();
    private readonly VoiceOutput _tts;

    public NarrationServer(VoiceOutput tts)
    {
        _tts = tts;
    }

    /// <summary>Start listening for narration messages.</summary>
    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        try
        {
            _listener.Start();
            AppLog.Info($"NarrationServer: listening on ws://localhost:{Port}");
            _ = Task.Run(AcceptLoopAsync, _cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error($"NarrationServer: failed to start: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener?.IsListening == true && !_cts.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();

                if (ctx.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketAsync(ctx));
                }
                else
                {
                    // Also accept plain HTTP POST for simple clients
                    if (ctx.Request.HttpMethod == "POST")
                    {
                        await HandleHttpPostAsync(ctx);
                    }
                    else
                    {
                        // Health check / info
                        var response = Encoding.UTF8.GetBytes(
                            "{\"status\":\"ok\",\"service\":\"voice-assistant-narration\"}");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(response);
                        ctx.Response.Close();
                    }
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                AppLog.Warn($"NarrationServer: accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        WebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            AppLog.Error($"NarrationServer: WebSocket accept failed: {ex.Message}");
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }

        var ws = wsCtx.WebSocket;
        var buf = new byte[8192];

        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                    ProcessNarration(text);

                    // Acknowledge
                    var ack = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    await ws.SendAsync(ack, WebSocketMessageType.Text, true, _cts.Token);
                }
            }

            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            ws.Dispose();
        }
    }

    private async Task HandleHttpPostAsync(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            ProcessNarration(text);

            var ack = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(ack);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"NarrationServer: HTTP POST error: {ex.Message}");
            ctx.Response.StatusCode = 500;
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    private void ProcessNarration(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        AppLog.Info($"NarrationServer: received narration ({text.Length} chars): {text[..Math.Min(80, text.Length)]}...");

        // Show in voice chat panel
        UIMessageBus.Push(MessageRole.Assistant, text, readAloud: false);

        // Speak via TTS
        _tts.Enqueue(text);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener?.Close();
        await Task.Delay(100);
        _cts.Dispose();
    }
}
