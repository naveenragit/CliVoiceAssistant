using VoiceAssistant.Auth;
using VoiceAssistant.Config;
using VoiceAssistant.Infrastructure;
using VoiceAssistant.Tools;
using VoiceAssistant.UI;

namespace VoiceAssistant;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ApplicationConfiguration.Initialize();

        var settings = UserSettings.Load();
        settings.Validate();
        var tokens   = new TokenProvider();

        // Apply copilot CLI injection settings from appsettings.json
        var appCfg = AppSettings.Load();
        var copilotCli = new CopilotCliTool
        {
            InstructionSuffix = appCfg.Copilot.InstructionSuffix,
            AutoSubmit        = appCfg.Copilot.AutoSubmit,
        };

        // Accept --session=<id> to resume a Copilot CLI session
        foreach (var arg in args)
        {
            if (arg.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
            {
                var sid = arg.Substring("--session=".Length).Trim();
                if (!string.IsNullOrEmpty(sid))
                {
                    copilotCli.ResumeSessionId = sid;
                    AppLog.Info($"Program: resuming Copilot CLI session {sid}");
                }
            }
        }

        // Ensure cleanup runs even on crash / TaskManager kill / Environment.Exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        Application.ApplicationExit += (_, _) => Cleanup();

        Application.Run(new MainForm(settings, tokens, copilotCli));
    }

    private static bool _cleaned;
    private static void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        EmbeddedTerminal.KillOrphans();
    }
}
