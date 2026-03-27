using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoiceAssistant.Audio;
using VoiceAssistant.Auth;
using VoiceAssistant.Config;
using VoiceAssistant.Infrastructure;
using VoiceAssistant.Networking;
using VoiceAssistant.Tools;

namespace VoiceAssistant.UI;

/// <summary>
/// Blank-canvas voice assistant window.
///
/// Architecture
/// ────────────
/// • Push-only display: nothing polls. <see cref="UIMessageBus"/> delivers
///   every message (user speech, model responses, system events, tool output)
///   by firing an event.  This form subscribes once and handles all rendering.
///
/// • TTS readback: messages with <c>ReadAloud = true</c> are also spoken via
///   <see cref="VoiceOutput"/> (Windows built-in TTS for non-Realtime sources).
///
/// • Connection: automatic on startup if settings are configured.  The
///   <see cref="SetupOverlay"/> appears when settings are missing or the user
///   clicks the ⚙ button.
///
/// • No polling, no timers, no background threads owned by this form.
/// </summary>
public sealed class MainForm : Form
{
    // ── DWM interop (dark title bar + grey border) ───────────────────────────
    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR            = 34;
    private const int DWMWA_CAPTION_COLOR            = 35;

    // ── Colors ────────────────────────────────────────────────────────────────
    private static readonly Color Canvas      = Color.FromArgb(10, 12, 20);
    private static readonly Color TitleBg     = Color.FromArgb(16, 20, 32);
    private static readonly Color BorderLine  = Color.FromArgb(35, 45, 65);
    private static readonly Color TextDefault = Color.FromArgb(226, 232, 240);
    private static readonly Color MutedColor  = Color.FromArgb(100, 116, 139);
    private static readonly Color ToolColor   = Color.FromArgb(251, 191,  36);   // amber
    private static readonly Color CopilotPink = Color.FromArgb(226, 178, 255);   // Copilot header pink

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly AppSettings    _cfg         = AppSettings.Load();
    private UserSettings            _settings;
    private readonly TokenProvider  _tokens;
    private readonly CopilotCliTool _copilotCli;
    private readonly VoiceOutput    _voiceOutputQueue = new();
    private readonly PttController  _pttController = new();
    private TokenRefreshService?    _tokenRefresh;
    private RealtimeClient?         _client;
    private ChatRenderer?           _chatRenderer;
    private bool                    _isConnected;
    private EmbeddedTerminal?       _terminal;   // embedded Copilot CLI terminal
    private NarrationServer?        _narration;  // local WebSocket server for CLI narration

    // ── Controls ─────────────────────────────────────────────────────────────
    private RichTextBox   _chat         = null!;
    private SetupOverlay  _overlay      = null!;
    private Panel         _titleBar     = null!;
    private Label         _titleLabel   = null!;
    private Button        _settingsBtn  = null!;
    private Button        _closeBtn     = null!;
    private Button        _micBtn       = null!;   // title-bar indicator
    private Button        _pttBtn       = null!;   // big push-to-talk button
    private Label         _pttLabel     = null!;   // "Hold to talk  /  Space"
    private Panel         _termPanel    = null!;   // hosts the embedded terminal
    private SplitContainer _splitter    = null!;   // terminal (top) / chat (bottom)
    private readonly ToolTip _tooltip   = new();   // single reusable instance — avoids GDI handle leak

    // Drag state for borderless window
    private bool _isDragging;
    private Point _dragStart;

    // ── Constructors ──────────────────────────────────────────────────────────

    public MainForm(UserSettings settings, TokenProvider tokens, CopilotCliTool copilotCli)
    {
        _settings   = settings;
        _tokens     = tokens;
        _copilotCli = copilotCli;
        BuildUI();
        SubscribeToBus();
    }

    // ── UI thread helper ───────────────────────────────────────────────────────

    private void RunOnUI(Action action)
    {
        if (InvokeRequired) Invoke(action);
        else action();
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        SuspendLayout();

        Text            = "Voice Assistant";
        Size            = new Size(1000, 800);
        MinimumSize     = new Size(900, 650);
        BackColor       = Canvas;
        ForeColor       = TextDefault;
        Font            = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = true;
        StartPosition   = FormStartPosition.CenterScreen;

        // ── Title bar ─────────────────────────────────────────────────────────
        _titleBar = new Panel
        {
            BackColor = TitleBg,
            Dock      = DockStyle.Top,
            Height    = 36,
        };
        _titleBar.Paint += (s, e) =>
        {
            // 1px bottom border
            using var pen = new Pen(BorderLine);
            e.Graphics.DrawLine(pen, 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
        };

        _titleLabel = new Label
        {
            Text      = "🎙  Voice Assistant",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = MutedColor,
            AutoSize  = true,
            Location  = new Point(12, 10),
        };

        _micBtn = new Button
        {
            Text      = "🔌",
            Font      = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(70, 80, 95),     // dull — not connected yet
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(26, 26),
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        _micBtn.FlatAppearance.BorderSize = 0;
        _micBtn.FlatAppearance.MouseOverBackColor = Color.Transparent;
        _micBtn.Click += OnMicClick;
        _tooltip.SetToolTip(_micBtn, "Click to connect / disconnect mic");

        _settingsBtn = MakeTitleButton("⚙", "Settings", _tooltip);
        _settingsBtn.Click += (_, _) => ShowSetup();

        _closeBtn = MakeTitleButton("✕", "Close", _tooltip);
        _closeBtn.ForeColor = Color.FromArgb(239, 68, 68);
        _closeBtn.Click += (_, _) => Close();

        _titleBar.Controls.AddRange(new Control[] { _titleLabel, _micBtn, _settingsBtn, _closeBtn });

        _titleBar.MouseDown += OnTitleBarMouseDown;
        _titleBar.MouseMove += OnTitleBarMouseMove;
        _titleBar.MouseUp   += (_, _) => _isDragging = false;
        _titleLabel.MouseDown += OnTitleBarMouseDown;
        _titleLabel.MouseMove += OnTitleBarMouseMove;
        _titleLabel.MouseUp   += (_, _) => _isDragging = false;

        _titleBar.Resize += (_, _) => LayoutTitleButtons();
        _titleBar.ClientSizeChanged += (_, _) => LayoutTitleButtons();

        // ── Chat window ───────────────────────────────────────────────────────
        _chat = new RichTextBox
        {
            BackColor   = Canvas,
            ForeColor   = TextDefault,
            BorderStyle = BorderStyle.None,
            Font        = new Font("Segoe UI", 10f),
            ReadOnly    = true,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Dock        = DockStyle.Fill,
            Padding     = new Padding(16, 10, 16, 10),
            WordWrap    = true,
        };
        _chatRenderer = new ChatRenderer(_chat);

        // ── Setup overlay (child — rendered on top of chat) ───────────────────
        _overlay = new SetupOverlay(_tokens) { Visible = false };
        _overlay.Confirmed += OnSetupConfirmed;

        // ── Bottom bar — PTT button ───────────────────────────────────────────
        var bottomBar = new Panel
        {
            BackColor = TitleBg,
            Dock      = DockStyle.Bottom,
            Height    = 110,
        };
        bottomBar.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderLine);
            e.Graphics.DrawLine(pen, 0, 0, bottomBar.Width, 0);
        };

        const int BtnSize = 72;
        _pttBtn = new Button
        {
            Size      = new Size(BtnSize, BtnSize),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(201, 60, 158),   // pink idle
            ForeColor = Color.White,
            Font      = new Font("Segoe UI Emoji", 22f),
            Text      = "🎙",
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        _pttBtn.FlatAppearance.BorderSize        = 0;
        _pttBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(170, 50, 134);
        MakeRound(_pttBtn);

        _pttLabel = new Label
        {
            Text      = "Hold to talk  ·  Space",
            ForeColor = MutedColor,
            Font      = new Font("Segoe UI", 8.5f),
            AutoSize  = true,
            Anchor    = AnchorStyles.Left | AnchorStyles.Top,
        };

        // Bottom bar only has the PTT button — label goes in the layout below
        bottomBar.Controls.Add(_pttBtn);
        bottomBar.Resize += (_, _) => CentrePttButton(bottomBar);

        // PTT button mouse events
        _pttBtn.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) PttPress(); };
        _pttBtn.MouseUp   += (_, e) => { if (e.Button == MouseButtons.Left) PttRelease(); };

        // Wire PttController events to RealtimeClient
        _pttController.PttStarted += () =>
        {
            _client?.StartPtt();
            _pttBtn.BackColor = Color.FromArgb(220, 38, 38);   // red = hot
            _pttBtn.Text      = "\ud83d\udd34";
            MakeRound(_pttBtn);
            _pttLabel.Text = "Listening\u2026  release to send";
            _voiceOutputQueue.SetSuppressed(true);
        };
        _pttController.PttEnded += () =>
        {
            _client?.StopPtt();
            _pttBtn.BackColor = Color.FromArgb(201, 60, 158);   // back to pink
            _pttBtn.Text      = "\ud83c\udf99";
            MakeRound(_pttBtn);
            _pttLabel.Text = "Hold to talk  \u00b7  Space";
            _voiceOutputQueue.SetSuppressed(false);
        };

        // ── Terminal panel (hosts embedded Copilot CLI conhost window) ──────────
        _termPanel = new Panel
        {
            BackColor = Color.FromArgb(10, 12, 20),
            Dock      = DockStyle.Fill,
        };

        // ── Split container: terminal (top) / voice chat + PTT (bottom) ───────
        _splitter = new SplitContainer
        {
            Dock            = DockStyle.Fill,
            Orientation     = Orientation.Horizontal,
            BackColor       = BorderLine,
            SplitterWidth   = 4,
            FixedPanel      = FixedPanel.Panel2,     // voice panel keeps its size
            Panel2MinSize   = 150,
        };
        _splitter.Panel1.BackColor = Color.FromArgb(10, 12, 20);
        _splitter.Panel2.BackColor = Canvas;

        _splitter.Panel1.Controls.Add(_termPanel);

        // ── Voice panel: TableLayoutPanel as root container ───────────────────
        var voiceLayout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            BackColor   = Canvas,
            ColumnCount = 1,
            RowCount    = 3,
            Margin      = Padding.Empty,
            Padding     = new Padding(12, 10, 12, 10),
        };
        voiceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // chat (fills)
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // mic label
        voiceLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100f));  // PTT bar

        // Chat fills the first row
        _chat.Dock        = DockStyle.Fill;
        _chat.BorderStyle = BorderStyle.None;

        // Mic label — AutoSize + Left|Top anchor, no Dock
        _pttLabel.AutoSize  = true;
        _pttLabel.Anchor    = AnchorStyles.Left | AnchorStyles.Top;
        _pttLabel.Margin    = new Padding(0, 4, 0, 4);

        // Bottom bar fills the third row
        bottomBar.Dock = DockStyle.Fill;

        voiceLayout.Controls.Add(_chat,     0, 0);
        voiceLayout.Controls.Add(_pttLabel, 0, 1);
        voiceLayout.Controls.Add(bottomBar, 0, 2);

        _splitter.Panel2.Controls.Add(voiceLayout);

        Controls.Add(_splitter);
        Controls.Add(_titleBar);
        Controls.Add(_overlay);    // above everything

        // Spacebar PTT — must set KeyPreview so form receives key events before controls
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                e.SuppressKeyPress = true;
                PttPress();
            }
        };
        KeyUp += (_, e) =>
        {
            if (e.KeyCode == Keys.Space) PttRelease();
        };

        ResumeLayout(false);

        Load        += OnLoad;
        FormClosing += OnFormClosing;
        Resize      += (_, _) => { LayoutTitleButtons(); CentrePttButton(bottomBar); };
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private async void OnLoad(object? sender, EventArgs e)
    {
        ApplyDarkWindowChrome();
        LayoutTitleButtons();

        // Set split position now that form has its actual size (65% terminal, 35% voice)
        try { _splitter.SplitterDistance = (int)((_splitter.Height - _splitter.SplitterWidth) * 0.65); }
        catch { /* ignore if form too small */ }

        // Register MCP server so the embedded CLI has the narrate tool
        McpInstaller.EnsureRegistered();

        // Start the narration server — route speech through Realtime API
        _narration = new NarrationServer(async text =>
        {
            if (_client != null)
                await _client.SpeakTextAsync(text);
        });
        _narration.Start();

        // Start the embedded Copilot CLI terminal
        _ = StartEmbeddedTerminalAsync();

        if (!_settings.IsConfigured)
        {
            ShowSetup();
            return;
        }

        UIMessageBus.PushSystem("Ready. Auto-connecting…", readAloud: false);
        await ConnectAsync();
    }

    private async Task StartEmbeddedTerminalAsync()
    {
        try
        {
            var workingDir = CopilotCliTool.GetProjectRoot();
            var sessionId = _copilotCli.ResumeSessionId;

            _terminal = new EmbeddedTerminal();
            await _terminal.StartAsync(_termPanel, workingDir, sessionId);

            // Wire terminal reference so voice commands are typed into the terminal
            _copilotCli.Terminal = _terminal;

            // Wire up voice command events — display in chat panel
            _copilotCli.OnCommandStarted += (command) =>
            {
                RunOnUI(() => OnVoiceCommandStarted(command));
                return Task.CompletedTask;
            };

            _copilotCli.OnDelta += (delta) =>
            {
                RunOnUI(() => OnVoiceDelta(delta));
                return Task.CompletedTask;
            };

            _copilotCli.OnCommandCompleted += (result) =>
            {
                RunOnUI(() => OnVoiceCommandCompleted(result));
                return Task.CompletedTask;
            };

            UIMessageBus.PushSystem("Terminal loaded — Copilot CLI is running above.", readAloud: false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to start terminal: {ex.Message}");
            UIMessageBus.PushSystem($"\u26a0 Terminal failed: {ex.Message}", readAloud: false);
        }
    }

    // ── Voice command display (delegated to ChatRenderer) ─────────────────────

    private void OnVoiceCommandStarted(string command)
    {
        _chatRenderer?.StartThinking();
    }

    private void OnVoiceDelta(string delta)
    {
        _chatRenderer?.HandleVoiceDelta(delta);
    }

    private void OnVoiceCommandCompleted(string result)
    {
        _chatRenderer?.HandleVoiceCommandCompleted();
    }

    // ── Push subscription ─────────────────────────────────────────────────────

    private void SubscribeToBus()
    {
        UIMessageBus.MessagePushed += OnMessagePushed;
    }

    private void OnMessagePushed(UIMessage msg)
    {
        // Always marshal to UI thread regardless of caller thread.
        if (InvokeRequired) { Invoke(() => OnMessagePushed(msg)); return; }

        _chatRenderer?.AppendMessage(msg);

        // TTS readback for non-Realtime messages (system / tool / injected text).
        // The Realtime API handles assistant voice itself; suppress TTS then.
        if (msg.ReadAloud && msg.Role != MessageRole.User)
            _voiceOutputQueue.Enqueue(msg.Text);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private async Task ConnectAsync()
    {
        SetMicState(connecting: true);
        UIMessageBus.PushSystem($"Connecting… (log: {AppLog.LogFilePath})", readAloud: false);
        try
        {
            _client = new RealtimeClient(_cfg, _settings, _tokens,
                new ToolRegistry(_copilotCli, _terminal));
            _client.StatusChanged += OnClientStatusChanged;
            await _client.ConnectAsync();
            _isConnected = true;
            SetMicState(connected: true);
            UIMessageBus.PushSystem("Connected — speak to start.", readAloud: false);

            // Start token health monitoring
            _tokenRefresh?.Stop();
            _tokenRefresh = new TokenRefreshService(_tokens);
            _tokenRefresh.TokenExpiring += OnTokenExpiring;
            _tokenRefresh.TokenRefreshed += OnTokenRefreshed;
            _tokenRefresh.TokenRefreshFailed += OnTokenRefreshFailed;
            _tokenRefresh.Start(_tokens.ExpiresOn);
        }
        catch (Exception ex)
        {
            AppLog.Error($"ConnectAsync failed: {ex.GetType().Name}: {ex.Message}");
            UIMessageBus.PushSystem($"⚠ Connection failed: {ex.Message}", readAloud: true);
            await (_client?.DisposeAsync() ?? ValueTask.CompletedTask);
            _client = null;
            SetMicState(connected: false);
        }
    }

    private async Task DisconnectAsync()
    {
        if (_client == null) return;
        _tokenRefresh?.Stop();
        await _client.DisconnectAsync();
        await _client.DisposeAsync();
        _client    = null;
        _isConnected = false;
        SetMicState(connected: false);
        UIMessageBus.PushSystem("Disconnected.", readAloud: false);
    }

    private async void OnMicClick(object? sender, EventArgs e)
    {
        if (_isConnected) await DisconnectAsync();
        else            await ConnectAsync();
    }

    /// <summary>
    /// Handles status changes from RealtimeClient — detects idle disconnects
    /// and updates the UI so the user knows to reconnect.
    /// </summary>
    private void OnClientStatusChanged(object? sender, StatusEventArgs e)
    {
        RunOnUI(() =>
        {
            if (e.State == ClientState.Idle && _isConnected)
            {
                _isConnected = false;
                _chatRenderer?.StopThinking();
                SetMicState(connected: false);
            }
        });
    }

    // ── Settings overlay ──────────────────────────────────────────────────────

    private void ShowSetup()
    {
        _overlay.Show(_settings);
    }

    private async void OnSetupConfirmed(UserSettings newSettings)
    {
        _settings = newSettings;
        UIMessageBus.PushSystem("Settings saved. Connecting…", readAloud: false);
        await ConnectAsync();
    }

    // ── Token refresh handlers ───────────────────────────────────────────────

    private void OnTokenExpiring(object? sender, int minutesRemaining)
    {
        RunOnUI(() =>
        {
            if (minutesRemaining <= 2)
                UIMessageBus.PushSystem($"⚠ Token expires in {minutesRemaining} min — refreshing…", readAloud: false);
        });
    }

    private async void OnTokenRefreshed(object? sender, string newToken)
    {
        if (InvokeRequired) { BeginInvoke(() => OnTokenRefreshed(sender, newToken)); return; }
        AppLog.Info("Token refreshed — reconnecting WebSocket");
        UIMessageBus.PushSystem("Token refreshed — reconnecting…", readAloud: false);
        await DisconnectAsync();
        await ConnectAsync();
    }

    private void OnTokenRefreshFailed(object? sender, string error)
    {
        RunOnUI(() => UIMessageBus.PushSystem($"⚠ Token refresh failed: {error}. Click mic to re-sign in.", readAloud: true));
    }

    // ── UI state helpers ──────────────────────────────────────────────────────

    private void SetMicState(bool connecting = false, bool connected = false)
    {
        if (connecting)
        {
            _micBtn.ForeColor  = ToolColor;
            _micBtn.Text       = "◌";
            _pttBtn.Enabled    = false;
            _pttBtn.BackColor  = Color.FromArgb(50, 35, 85);
        }
        else if (connected)
        {
            _micBtn.ForeColor  = CopilotPink;
            _micBtn.Text       = "🎙";
            _pttBtn.Enabled    = true;
            _pttBtn.BackColor  = Color.FromArgb(201, 60, 158);   // pink ready
            MakeRound(_pttBtn);
            _voiceOutputQueue.SetSuppressed(false);
        }
        else
        {
            _micBtn.ForeColor  = Color.FromArgb(70, 80, 95);    // dull grey
            _micBtn.Text       = "🔌";
            _pttBtn.Enabled    = false;
            _pttBtn.BackColor  = Color.FromArgb(50, 35, 85);
            MakeRound(_pttBtn);
        }

        // Update tooltip
        _tooltip.SetToolTip(_micBtn, connected ? "Connected — click to disconnect"
                                               : "Disconnected — click to connect");
    }

    // ── Push-to-talk (delegated to PttController) ──────────────────────────────

    private void PttPress()  => _pttController.Press(_isConnected);
    private void PttRelease() => _pttController.Release();

    // ── Title bar helpers ─────────────────────────────────────────────────────

    private static Button MakeTitleButton(string text, string tip, ToolTip tooltip)
    {
        var btn = new Button
        {
            Text      = text,
            Font      = new Font("Segoe UI", 9f),
            ForeColor = MutedColor,
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(28, 28),
            TabStop   = false,
            Cursor    = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize        = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 50, 70);
        tooltip.SetToolTip(btn, tip);
        return btn;
    }

    private static void MakeRound(Button btn)
    {
        int r = Math.Min(btn.Width, btn.Height);
        var path = new GraphicsPath();
        path.AddEllipse(0, 0, r, r);
        btn.Region = new Region(path);
    }

    private void CentrePttButton(Panel bar)
    {
        int cx = bar.ClientSize.Width / 2;
        _pttBtn.Location = new Point(cx - _pttBtn.Width / 2, 14);
    }

    private void LayoutTitleButtons()
    {
        int x = _titleBar.ClientSize.Width - 4;
        foreach (var btn in new[] { _closeBtn, _settingsBtn, _micBtn })
        {
            x -= btn.Width + 2;
            btn.Location = new Point(x, (_titleBar.Height - btn.Height) / 2);
        }
    }

    /// <summary>
    /// Uses DWM to set dark mode, grey border, and dark caption bar so the
    /// OS window chrome blends with the app's dark theme.
    /// </summary>
    private void ApplyDarkWindowChrome()
    {
        try
        {
            var hwnd = Handle;

            // Enable immersive dark mode (dark scrollbars + dark title bar text)
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Border color — COLORREF is 0x00BBGGRR
            int greyBorder = 0x00464646; // RGB(70, 70, 70) → grey
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref greyBorder, sizeof(int));

            // Caption (title bar) background — match our Canvas color
            int captionColor = Canvas.B << 16 | Canvas.G << 8 | Canvas.R; // COLORREF
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            // Dark scrollbar on the RichTextBox chat control
            SetWindowTheme(_chat.Handle, "DarkMode_Explorer", null);
        }
        catch
        {
            // DWM attributes require Windows 11 22H2+; silently ignore on older OS
        }
    }

    // ── Borderless window drag ────────────────────────────────────────────────

    private void OnTitleBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _isDragging  = true;
        _dragStart = e.Location;
        if (sender is Control c) _dragStart = c.PointToScreen(e.Location);
        _dragStart = new Point(_dragStart.X - Left, _dragStart.Y - Top);
    }

    private void OnTitleBarMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var screen = (sender is Control c) ? c.PointToScreen(e.Location) : Cursor.Position;
        Location = new Point(screen.X - _dragStart.X, screen.Y - _dragStart.Y);
    }

    // ── Window close ─────────────────────────────────────────────────────────

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        UIMessageBus.MessagePushed -= OnMessagePushed;

        // Clean up embedded terminal process
        _chatRenderer?.Dispose();
        _terminal?.Dispose();
        _terminal = null;

        // Stop and dispose token refresh monitor
        _tokenRefresh?.Stop();
        _tokenRefresh?.Dispose();
        _tokenRefresh = null;

        // Dispose shared ToolTip
        _tooltip.Dispose();

        // Clean up narration server
        if (_narration != null)
            await _narration.DisposeAsync();

        // Unregister MCP server from user config
        McpInstaller.Unregister();

        if (_client != null)
        {
            e.Cancel = true;
            await DisconnectAsync();
            await _voiceOutputQueue.DisposeAsync();
            Close();
        }
        else
        {
            await _voiceOutputQueue.DisposeAsync();
        }
    }
}
