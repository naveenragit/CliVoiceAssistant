using System.Net;
using System.Net.WebSockets;
using System.Text;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.Networking;

/// <summary>
/// Local WebSocket server on ws://localhost:9877 that receives narration text
/// from Copilot CLI (via MCP tool) and speaks it aloud through the Voice Assistant.
/// </summary>
public sealed class NarrationServer : IAsyncDisposable
{
    public const int Port = 9877;

    private HttpListener? _narrationHttpListener;
    private CancellationTokenSource _cancellationTokenSource = new();
    private readonly Func<string, Task>? _speakAsync;

    /// <param name="speakAsync">Async callback to speak narration text via Realtime API.</param>
    public NarrationServer(Func<string, Task>? speakAsync = null)
    {
        _speakAsync = speakAsync;
    }

    /// <summary>Start listening for narration messages.</summary>
    public void Start()
    {
        _narrationHttpListener = new HttpListener();
        _narrationHttpListener.Prefixes.Add($"http://localhost:{Port}/");
        try
        {
            _narrationHttpListener.Start();
            AppLog.Info($"NarrationServer: listening on ws://localhost:{Port}");
            _ = Task.Run(AcceptLoopAsync, _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error($"NarrationServer: failed to start: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_narrationHttpListener?.IsListening == true && !_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var httpContext = await _narrationHttpListener.GetContextAsync();

                if (httpContext.Request.IsWebSocketRequest)
                {
                    _ = Task.Run(() => HandleWebSocketAsync(httpContext));
                }
                else
                {
                    // Also accept plain HTTP POST for simple clients
                    if (httpContext.Request.HttpMethod == "POST")
                    {
                        await HandleHttpPostAsync(httpContext);
                    }
                    else
                    {
                        // Health check / info
                        var response = Encoding.UTF8.GetBytes(
                            "{\"status\":\"ok\",\"service\":\"voice-assistant-narration\"}");
                        httpContext.Response.StatusCode = 200;
                        httpContext.Response.ContentType = "application/json";
                        await httpContext.Response.OutputStream.WriteAsync(response);
                        httpContext.Response.Close();
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

    private async Task HandleWebSocketAsync(HttpListenerContext httpContext)
    {
        WebSocketContext webSocketContext;
        try
        {
            webSocketContext = await httpContext.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            AppLog.Error($"NarrationServer: WebSocket accept failed: {ex.Message}");
            httpContext.Response.StatusCode = 500;
            httpContext.Response.Close();
            return;
        }

        var webSocket = webSocketContext.WebSocket;
        var receiveBuffer = new byte[8192];

        try
        {
            while (webSocket.State == WebSocketState.Open && !_cancellationTokenSource.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(receiveBuffer, _cancellationTokenSource.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                    ProcessNarration(text);

                    // Acknowledge
                    var acknowledgementJson = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                    await webSocket.SendAsync(acknowledgementJson, WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                }
            }

            if (webSocket.State == WebSocketState.Open)
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            webSocket.Dispose();
        }
    }

    private async Task HandleHttpPostAsync(HttpListenerContext httpContext)
    {
        try
        {
            using var reader = new System.IO.StreamReader(httpContext.Request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            ProcessNarration(text);

            var acknowledgementJson = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
            httpContext.Response.StatusCode = 200;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.OutputStream.WriteAsync(acknowledgementJson);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"NarrationServer: HTTP POST error: {ex.Message}");
            httpContext.Response.StatusCode = 500;
        }
        finally
        {
            httpContext.Response.Close();
        }
    }

    private void ProcessNarration(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        AppLog.Info($"NarrationServer: received narration ({text.Length} chars): {text[..Math.Min(80, text.Length)]}...");

        // Don't push to UI here — HandleTranscriptDone will add the message
        // when the Realtime API speaks it, avoiding duplicates.

        // Speak via Realtime API (natural voice)
        if (_speakAsync != null)
        {
            _ = Task.Run(async () =>
            {
                try { await _speakAsync(text); }
                catch (Exception ex) { AppLog.Warn($"NarrationServer: speak failed: {ex.Message}"); }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _narrationHttpListener?.Stop();
        _narrationHttpListener?.Close();
        await Task.Delay(100);
        _cancellationTokenSource.Dispose();
    }
}
