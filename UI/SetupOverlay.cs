using VoiceAssistant.Auth;
using VoiceAssistant.Config;
using VoiceAssistant.Infrastructure;

namespace VoiceAssistant.UI;

/// <summary>
/// Inline setup overlay — a Panel child of MainForm that appears over the
/// blank canvas only when the user needs to enter connection settings.
///
/// Shows endpoint, deployment, and auth mode (Azure AD or API Key).
/// On save it writes config to disk / vault, then hides itself.
///
/// Trigger visibility via <see cref="Show(UserSettings)"/> /
/// <see cref="Hide"/>.
///
/// Wire <see cref="Confirmed"/> to act when setup is complete.
/// </summary>
public sealed class SetupOverlay : Panel
{
    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<UserSettings>? Confirmed;

    // ── Controls ─────────────────────────────────────────────────────────────
    private readonly TextBox     _endpointBox;
    private readonly TextBox     _deploymentBox;
    private readonly RadioButton _radioAad;
    private readonly RadioButton _radioApiKey;
    private readonly Panel       _aadSection;
    private readonly Panel       _apiKeySection;
    private readonly Button      _signInBtn;
    private readonly Label       _authStatusLabel;
    private readonly ProgressBar _spinner;
    private readonly TextBox     _apiKeyBox;
    private readonly Button      _saveBtn;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly TokenProvider           _tokens;
    private bool                             _isAuthenticated;
    private CancellationTokenSource          _signInCts = new();
    private bool UseAad => _radioAad.Checked;

    // ── Colors ────────────────────────────────────────────────────────────────
    private static readonly Color BgCard     = Color.FromArgb(22, 28, 40);
    private static readonly Color Border     = Color.FromArgb(55, 65, 81);
    private static readonly Color AccentColor = Color.FromArgb(99, 102, 241);
    private static readonly Color TextColor  = Color.FromArgb(226, 232, 240);
    private static readonly Color MutedColor = Color.FromArgb(100, 116, 139);
    private static readonly Color OkGreen    = Color.FromArgb(16, 185, 129);
    private static readonly Color ErrorRed   = Color.FromArgb(239, 68, 68);
    private static readonly Color VaultAmber = Color.FromArgb(251, 191, 36);

    public SetupOverlay(TokenProvider tokens)
    {
        _tokens = tokens;

        // ── Outer panel (full-form backdrop) ─────────────────────────────────
        BackColor = Color.FromArgb(180, 10, 12, 20);     // semi-transparent dark tint
        Visible   = false;
        Dock      = DockStyle.Fill;

        // ── Inner card ────────────────────────────────────────────────────────
        var card = new Panel
        {
            BackColor = BgCard,
            Size      = new Size(500, 520),
            Padding   = new Padding(28),
        };
        // Painted border
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        Controls.Add(card);
        Resize += (_, _) => CentreCard(card);
        CentreCard(card);

        // ─────────── Build card contents ─────────────────────────────────────
        int lx = 28, y = 28, w = card.ClientSize.Width - 56;   // 28px padding each side

        // Title
        card.Controls.Add(new Label
        {
            Text      = "Connection Settings",
            Font      = new Font("Segoe UI", 13f),
            ForeColor = TextColor,
            AutoSize  = true,
            Location  = new Point(lx, y),
        });
        y += 38;

        card.Controls.Add(new Label
        {
            Text      = "Enter your Azure AI Foundry endpoint and model details.",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = MutedColor,
            AutoSize  = false,
            Size      = new Size(w, 20),
            Location  = new Point(lx, y),
        });
        y += 34;

        // Endpoint
        AddLabel(card, "Endpoint or Foundry connection string", lx, y);
        _endpointBox = AddTextBox(card, lx, y + 20, w, 26,
            "https://resource.openai.azure.com  or  eastus.api.azureml.ms;sub;rg;proj");
        y += 56;

        // Deployment
        AddLabel(card, "Model deployment name", lx, y);
        _deploymentBox = AddTextBox(card, lx, y + 20, w, 26, "gpt-4o-realtime-preview");
        _deploymentBox.Text = "gpt-4o-realtime-preview";
        y += 56;

        // Divider
        card.Controls.Add(new Panel
        {
            BackColor = Border,
            Size      = new Size(w, 1),
            Location  = new Point(lx, y),
        });
        y += 12;

        // Auth mode radios
        AddLabel(card, "Authentication", lx, y);
        y += 22;
        _radioAad = new RadioButton
        {
            Text      = "Azure AD — interactive browser sign-in",
            Checked   = true, AutoSize = true,
            Location  = new Point(lx, y),
            ForeColor = TextColor,
        };
        _radioApiKey = new RadioButton
        {
            Text      = "API Key — stored in Windows Credential Manager",
            AutoSize  = true,
            Location  = new Point(lx, y + 24),
            ForeColor = TextColor,
        };
        card.Controls.AddRange(new Control[] { _radioAad, _radioApiKey });
        y += 54;

        // ── AAD section ───────────────────────────────────────────────────────
        _aadSection = new Panel
        {
            Location  = new Point(lx, y),
            Size      = new Size(w, 68),
            BackColor = Color.Transparent,
        };
        _signInBtn = new Button
        {
            Text      = "⮕  Sign in with Microsoft",
            BackColor = AccentColor, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(200, 30), Location = new Point(0, 0),
            Cursor    = Cursors.Hand, Font = new Font("Segoe UI", 9.5f),
        };
        _signInBtn.FlatAppearance.BorderSize = 0;
        _signInBtn.Click += OnSignInClick;
        _authStatusLabel = new Label
        {
            Text      = "", ForeColor = MutedColor,
            AutoSize  = false, Size = new Size(w - 210, 30),
            Location  = new Point(208, 0), TextAlign = ContentAlignment.MiddleLeft,
        };
        _spinner = new ProgressBar
        {
            Style    = ProgressBarStyle.Marquee,
            Size     = new Size(w, 4),
            Location = new Point(0, 36), Visible = false,
        };
        _aadSection.Controls.AddRange(new Control[] { _signInBtn, _authStatusLabel, _spinner });

        // ── API Key section ───────────────────────────────────────────────────
        _apiKeySection = new Panel
        {
            Location  = new Point(lx, y),
            Size      = new Size(w, 68),
            BackColor = Color.Transparent,
            Visible   = false,
        };
        _apiKeySection.Controls.Add(new Label
        {
            Text      = "🔒 Saved to Windows Credential Manager — not stored on disk.",
            ForeColor = VaultAmber, Font = new Font("Segoe UI", 8.5f),
            AutoSize  = false, Size = new Size(w, 18), Location = new Point(0, 0),
        });
        _apiKeyBox = new TextBox
        {
            Location     = new Point(0, 24), Size = new Size(w, 26),
            BackColor    = Color.FromArgb(30, 41, 59), ForeColor = TextColor,
            BorderStyle  = BorderStyle.FixedSingle,
            Font         = new Font("Segoe UI", 9.5f),
            PasswordChar = '●',
            PlaceholderText = "Paste your Azure OpenAI API key",
        };
        _apiKeyBox.TextChanged += (_, _) => UpdateSaveButton();
        _apiKeySection.Controls.Add(_apiKeyBox);

        card.Controls.AddRange(new Control[] { _aadSection, _apiKeySection });
        y += 74;

        // ── Save button ───────────────────────────────────────────────────────
        _saveBtn = new Button
        {
            Text      = "Save & Connect",
            BackColor = OkGreen, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(140, 34), Location = new Point(lx + w - 140, y),
            Enabled   = false, Cursor = Cursors.Hand,
            Font      = new Font("Segoe UI", 9.5f),
        };
        _saveBtn.FlatAppearance.BorderSize = 0;
        _saveBtn.Click += OnSave;
        card.Controls.Add(_saveBtn);

        // ── Wire up ───────────────────────────────────────────────────────────
        _radioAad.CheckedChanged    += (_, _) => { SyncSections(); UpdateSaveButton(); };
        _radioApiKey.CheckedChanged += (_, _) => { SyncSections(); UpdateSaveButton(); };
        _endpointBox.TextChanged    += (_, _) => UpdateSaveButton();
        _deploymentBox.TextChanged  += (_, _) => UpdateSaveButton();

        // Click on the backdrop (outside the card) cancels if already configured
        Click += OnBackdropClick;
    }

    // ── Show / Hide ───────────────────────────────────────────────────────────

    public new void Show(UserSettings existing)
    {
        // Pre-populate fields from existing settings
        if (!string.IsNullOrEmpty(existing.Endpoint))
            _endpointBox.Text = existing.Endpoint;
        if (!string.IsNullOrEmpty(existing.DeploymentName))
            _deploymentBox.Text = existing.DeploymentName;

        _radioAad.Checked    = existing.AuthMode != "apikey";
        _radioApiKey.Checked = existing.AuthMode == "apikey";

        // If already signed in via AAD, reflect that
        _isAuthenticated = existing.IsConfigured && existing.AuthMode == "aad";
        if (_isAuthenticated) SetAuthStatus("✓ Previously signed in", ok: true);

        SyncSections();
        UpdateSaveButton();
        Visible = true;
        BringToFront();
    }

    public new void Hide()
    {
        Visible = false;
        _signInCts.Cancel();
    }

    // ── Backdrop click — dismiss if already configured ────────────────────────

    private void OnBackdropClick(object? sender, EventArgs e)
    {
        var card = Controls[0];
        if (!card.Bounds.Contains(PointToClient(MousePosition)))
        {
            // Only dismiss if already configured
            var existing = UserSettings.Load();
            if (existing.IsConfigured) Hide();
        }
    }

    // ── Sign-in ───────────────────────────────────────────────────────────────

    private async void OnSignInClick(object? sender, EventArgs e)
    {
        _signInCts         = new CancellationTokenSource();
        _signInBtn.Enabled = false;
        _spinner.Visible   = true;

        bool ok = await _tokens.AuthenticateInteractiveAsync(
            msg => InvokeIfRequired(() => SetAuthStatus(msg, ok: null)),
            _signInCts.Token);

        _isAuthenticated     = ok;
        _spinner.Visible   = false;
        _signInBtn.Enabled = true;
        SetAuthStatus(ok ? "✓ Signed in" : "Failed — try again", ok);
        UpdateSaveButton();
    }

    private void SetAuthStatus(string msg, bool? ok)
    {
        _authStatusLabel.Text      = msg;
        _authStatusLabel.ForeColor = ok == true  ? OkGreen
                                   : ok == false ? ErrorRed
                                   : MutedColor;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void OnSave(object? sender, EventArgs e)
    {
        var settings = new UserSettings
        {
            Endpoint       = UserSettings.NormalizeEndpoint(_endpointBox.Text),
            DeploymentName = _deploymentBox.Text.Trim(),
            AuthMode       = UseAad ? "aad" : "apikey",
            IsConfigured   = true,
        };
        settings.Save();

        if (!UseAad)
            CredentialStore.StoreApiKey(_apiKeyBox.Text.Trim());

        Hide();
        Confirmed?.Invoke(settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SyncSections()
    {
        _aadSection.Visible    = UseAad;
        _apiKeySection.Visible = !UseAad;
    }

    private void UpdateSaveButton()
    {
        bool endpointOk   = UserSettings.IsValidEndpoint(_endpointBox.Text);
        bool deploymentOk = !string.IsNullOrWhiteSpace(_deploymentBox.Text);
        bool authOk       = UseAad ? _isAuthenticated : !string.IsNullOrWhiteSpace(_apiKeyBox.Text);
        _saveBtn.Enabled  = endpointOk && deploymentOk && authOk;
    }

    private static void CentreCard(Panel card)
    {
        if (card.Parent is not { } parent) return;
        card.Location = new Point(
            (parent.ClientSize.Width  - card.Width)  / 2,
            (parent.ClientSize.Height - card.Height) / 2);
    }

    private void InvokeIfRequired(Action a)
    {
        if (InvokeRequired) Invoke(a); else a();
    }

    // ── Label / TextBox factory ───────────────────────────────────────────────

    private static void AddLabel(Control parent, string text, int x, int y)
        => parent.Controls.Add(new Label
        {
            Text = text, ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(x, y),
        });

    private static TextBox AddTextBox(Control parent, int x, int y, int w, int h, string placeholder)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y), Size = new Size(w, h),
            BackColor = Color.FromArgb(30, 41, 59), ForeColor = Color.FromArgb(226, 232, 240),
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5f),
            PlaceholderText = placeholder,
        };
        parent.Controls.Add(tb);
        return tb;
    }
}
