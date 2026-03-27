using VoiceAssistant.Auth;
using VoiceAssistant.Config;

namespace VoiceAssistant.UI;

/// <summary>
/// First-run setup dialog.
///
/// Collects:
///   1. Azure AI Foundry / Azure OpenAI endpoint  (non-secret → JSON config)
///   2. Model deployment name                     (non-secret → JSON config)
///   3. Auth mode:
///        Azure AD  → interactive browser login via MSAL; token cache is
///                    DPAPI-encrypted by Azure.Identity on disk.
///        API Key   → static key saved to Windows Credential Manager
///                    (Windows.Security.Credentials.PasswordVault), never
///                    written to disk in plaintext.
/// </summary>
public sealed class SetupDialog : Form
{
    // ── Controls ─────────────────────────────────────────────────────────────
    private readonly TextBox     _endpointBox;
    private readonly TextBox     _deploymentBox;
    private readonly RadioButton _radioAad;
    private readonly RadioButton _radioApiKey;
    private readonly Panel       _aadPanel;
    private readonly Panel       _apiKeyPanel;
    private readonly Button      _signInBtn;
    private readonly Label       _authStatusLabel;
    private readonly ProgressBar _spinner;
    private readonly TextBox     _apiKeyBox;
    private readonly Label       _apiKeyVaultHint;
    private readonly Button      _okBtn;
    private readonly Button      _cancelBtn;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly TokenProvider     _tokens;
    private bool                       _authenticated;   // AAD mode
    private CancellationTokenSource    _signInCts = new();
    private bool UseAad => _radioAad.Checked;

    public UserSettings Result { get; private set; } = new();

    // ── Colors ────────────────────────────────────────────────────────────────
    private static readonly Color BgColor     = Color.FromArgb(15,  17, 23);
    private static readonly Color PanelColor  = Color.FromArgb(30,  41, 59);
    private static readonly Color AccentColor = Color.FromArgb(99, 102, 241);
    private static readonly Color TextColor   = Color.FromArgb(226, 232, 240);
    private static readonly Color MutedColor  = Color.FromArgb(100, 116, 139);
    private static readonly Color OkGreen     = Color.FromArgb(16, 185, 129);
    private static readonly Color ErrorRed    = Color.FromArgb(239,  68,  68);
    private static readonly Color VaultColor  = Color.FromArgb(251, 191,  36);  // amber for vault label

    public SetupDialog(TokenProvider tokens)
    {
        _tokens = tokens;
        SuspendLayout();

        Text            = "Voice Assistant — Setup";
        Size            = new Size(540, 560);
        MinimumSize     = Size;
        MaximumSize     = Size;
        BackColor       = BgColor;
        ForeColor       = TextColor;
        Font            = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;

        // ── Hero ──────────────────────────────────────────────────────────────
        var heroPanel = new Panel { BackColor = PanelColor, Dock = DockStyle.Top, Height = 76 };
        heroPanel.Controls.Add(new Label
        {
            Text = "🎙️  Voice Assistant — First Run Setup",
            Font = new Font("Segoe UI", 12f), ForeColor = TextColor,
            AutoSize = false, Size = new Size(500, 38), Location = new Point(22, 8),
        });
        heroPanel.Controls.Add(new Label
        {
            Text = "Configure your Azure AI Foundry connection once — settings are stored securely.",
            Font = new Font("Segoe UI", 9f), ForeColor = MutedColor,
            AutoSize = false, Size = new Size(500, 20), Location = new Point(22, 48),
        });

        int lx = 26, y = 96;

        // ── Endpoint ──────────────────────────────────────────────────────────
        AddLabel(this, "Azure AI Foundry / OpenAI Endpoint", lx, y);
        _endpointBox = AddTextBox(this, lx, y + 20, 484, 26,
            "https://your-resource.openai.azure.com  or  eastus.api.azureml.ms;sub;rg;project");
        y += 58;

        // ── Deployment ────────────────────────────────────────────────────────
        AddLabel(this, "Model Deployment Name", lx, y);
        _deploymentBox = AddTextBox(this, lx, y + 20, 484, 26, "gpt-4o-realtime-preview");
        _deploymentBox.Text = "gpt-4o-realtime-preview";
        y += 58;

        // ── Divider ───────────────────────────────────────────────────────────
        Controls.Add(new Panel { BackColor = PanelColor, Size = new Size(484, 1), Location = new Point(lx, y) });
        y += 14;

        AddLabel(this, "Authentication Method", lx, y);
        y += 24;

        // ── Radio buttons ─────────────────────────────────────────────────────
        _radioAad = new RadioButton
        {
            Text = "Azure AD — sign in with Microsoft (recommended)",
            Checked = true, AutoSize = true, Location = new Point(lx, y),
            ForeColor = TextColor,
        };
        _radioApiKey = new RadioButton
        {
            Text = "API Key — stored in Windows Credential Manager",
            AutoSize = true, Location = new Point(lx, y + 26),
            ForeColor = TextColor,
        };
        Controls.AddRange(new Control[] { _radioAad, _radioApiKey });
        y += 60;

        // ── AAD panel ─────────────────────────────────────────────────────────
        _aadPanel = new Panel
        {
            Location = new Point(lx, y), Size = new Size(484, 110),
            BackColor = Color.Transparent,
        };
        _aadPanel.Controls.Add(new Label
        {
            Text = "Your browser will open for Microsoft sign-in.\n" +
                   "Credentials are handled entirely by Microsoft (MSAL).\n" +
                   "The token cache is encrypted with Windows DPAPI on disk.",
            ForeColor = MutedColor, Font = new Font("Segoe UI", 8.5f),
            AutoSize = false, Size = new Size(484, 50), Location = new Point(0, 0),
        });
        _signInBtn = new Button
        {
            Text = "⮕  Sign in with Microsoft",
            BackColor = AccentColor, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Size = new Size(210, 32), Location = new Point(0, 56), Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5f),
        };
        _signInBtn.FlatAppearance.BorderSize = 0;
        _signInBtn.Click += OnSignInClick;
        _authStatusLabel = new Label
        {
            Text = "", ForeColor = MutedColor,
            AutoSize = false, Size = new Size(260, 32), Location = new Point(218, 56),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _spinner = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee, Size = new Size(484, 4),
            Location = new Point(0, 92), Visible = false,
        };
        _aadPanel.Controls.AddRange(new Control[] { _signInBtn, _authStatusLabel, _spinner });

        // ── API Key panel ─────────────────────────────────────────────────────
        _apiKeyPanel = new Panel
        {
            Location = new Point(lx, y), Size = new Size(484, 110),
            BackColor = Color.Transparent, Visible = false,
        };
        _apiKeyVaultHint = new Label
        {
            Text = "🔒 Stored in Windows Credential Manager — never written to disk in plaintext.\n" +
                   "Visible under: Control Panel → Credential Manager → Windows Credentials.",
            ForeColor = VaultColor, Font = new Font("Segoe UI", 8.5f),
            AutoSize = false, Size = new Size(484, 40), Location = new Point(0, 0),
        };
        AddLabel(_apiKeyPanel, "API Key", 0, 48);
        _apiKeyBox = new TextBox
        {
            Location = new Point(0, 68), Size = new Size(484, 26),
            BackColor = Color.FromArgb(30, 41, 59), ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9.5f),
            PasswordChar = '●',
        };
        _apiKeyBox.TextChanged += (_, _) => UpdateOkButton();
        _apiKeyPanel.Controls.AddRange(new Control[] { _apiKeyVaultHint, _apiKeyBox });

        Controls.AddRange(new Control[] { _aadPanel, _apiKeyPanel });
        y += 118;

        // ── Buttons ───────────────────────────────────────────────────────────
        _okBtn = new Button
        {
            Text = "Finish", BackColor = OkGreen, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Size = new Size(100, 32),
            Location = new Point(ClientSize.Width - 232, y), Enabled = false, Cursor = Cursors.Hand,
        };
        _okBtn.FlatAppearance.BorderSize = 0;
        _okBtn.Click += OnOkClick;

        _cancelBtn = new Button
        {
            Text = "Cancel", BackColor = PanelColor, ForeColor = MutedColor,
            FlatStyle = FlatStyle.Flat, Size = new Size(100, 32),
            Location = new Point(ClientSize.Width - 122, y),
            DialogResult = DialogResult.Cancel, Cursor = Cursors.Hand,
        };
        _cancelBtn.FlatAppearance.BorderSize = 0;

        Controls.AddRange(new Control[] { _okBtn, _cancelBtn, heroPanel });

        // ── Radio wiring ──────────────────────────────────────────────────────
        _radioAad.CheckedChanged    += OnAuthModeChanged;
        _radioApiKey.CheckedChanged += OnAuthModeChanged;

        // ── Text-change wiring ────────────────────────────────────────────────
        _endpointBox.TextChanged   += (_, _) => UpdateOkButton();
        _deploymentBox.TextChanged += (_, _) => UpdateOkButton();

        ResumeLayout(false);
    }

    // ── Auth mode toggle ──────────────────────────────────────────────────────

    private void OnAuthModeChanged(object? sender, EventArgs e)
    {
        _aadPanel.Visible    = UseAad;
        _apiKeyPanel.Visible = !UseAad;
        UpdateOkButton();
    }

    // ── Azure AD sign-in ──────────────────────────────────────────────────────

    private async void OnSignInClick(object? sender, EventArgs e)
    {
        _signInCts         = new CancellationTokenSource();
        _signInBtn.Enabled = false;
        _spinner.Visible   = true;

        bool ok = await _tokens.AuthenticateInteractiveAsync(
            msg => Invoke(() => SetAuthStatus(msg, ok: null)),
            _signInCts.Token);

        _authenticated     = ok;
        _spinner.Visible   = false;
        _signInBtn.Enabled = true;

        SetAuthStatus(ok ? "✓ Signed in" : "Sign-in failed — try again", ok);
        UpdateOkButton();
    }

    private void SetAuthStatus(string msg, bool? ok)
    {
        _authStatusLabel.Text      = msg;
        _authStatusLabel.ForeColor = ok == true  ? OkGreen
                                   : ok == false ? ErrorRed
                                   : MutedColor;
    }

    // ── Finish ────────────────────────────────────────────────────────────────

    private void OnOkClick(object? sender, EventArgs e)
    {
        var endpoint = UserSettings.NormalizeEndpoint(_endpointBox.Text);

        if (!UseAad)
        {
            // Save API key to Windows Credential Manager — not to the JSON file.
            CredentialStore.StoreApiKey(_apiKeyBox.Text.Trim());
        }

        Result = new UserSettings
        {
            Endpoint       = endpoint,
            DeploymentName = _deploymentBox.Text.Trim(),
            AuthMode       = UseAad ? "aad" : "apikey",
            IsConfigured   = true,
        };
        Result.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateOkButton()
    {
        bool endpointOk    = UserSettings.IsValidEndpoint(_endpointBox.Text);
        bool deploymentOk  = !string.IsNullOrWhiteSpace(_deploymentBox.Text);
        bool authOk        = UseAad ? _authenticated : !string.IsNullOrWhiteSpace(_apiKeyBox.Text);
        _okBtn.Enabled     = endpointOk && deploymentOk && authOk;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddLabel(Control parent, string text, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = text, ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(x, y),
        });
    }

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

    protected override void OnFormClosing(FormClosingEventArgs e) { _signInCts.Cancel(); base.OnFormClosing(e); }
}
