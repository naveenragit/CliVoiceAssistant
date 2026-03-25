# Voice Assistant v4 — Copilot CLI Plugin

A Windows Forms voice assistant powered by **Azure OpenAI `gpt-4o-realtime-preview`**.
Speak commands, see them typed into an embedded Copilot CLI terminal, and hear responses read aloud.

## Installation

### As a Copilot CLI plugin (recommended)

Install directly from GitHub:
```bash
copilot plugin install naveenragit/CliVoiceAssistant
```

Then launch from any Copilot CLI session:
```
/voice
```

### From a local clone

```bash
git clone https://github.com/naveenragit/CliVoiceAssistant.git
copilot plugin install ./CliVoiceAssistant
```

### Standalone (no plugin install)

```bash
git clone https://github.com/naveenragit/CliVoiceAssistant.git
cd CliVoiceAssistant
dotnet run
```

## Prerequisites

- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** — `winget install Microsoft.DotNet.SDK.8`
- **Windows 10/11**
- **Azure OpenAI resource** with a `gpt-4o-realtime-preview` deployment
- **Microphone + speakers**
- **GitHub Copilot CLI** — `winget install GitHub.Copilot` (required for plugin mode)

## Configuration

On first launch, a setup overlay prompts for:
- Azure OpenAI endpoint
- API key or Azure AD sign-in
- Deployment name (e.g., `gpt-4o-realtime-preview`)

Settings are saved to `~/.copilot/voice-assistant-settings.json`.

## Usage

| Action | Result |
|--------|--------|
| Hold 🎙 button / Space | Push-to-talk — speak your command |
| Release | Transcription → typed into terminal → Copilot responds |
| Click 🔌 / 🎙 | Connect / disconnect mic |
| Click ⚙ | Open settings |

## Layout

```
┌─────────────────────────────┐
│  🎙 Voice Assistant    ⚙ ✕  │  ← title bar
├─────────────────────────────┤
│                             │
│   Embedded Copilot CLI      │  ← native conhost terminal (65%)
│   (commands + responses)    │
│                             │
├─────────────────────────────┤
│   Voice chat transcript     │  ← voice panel (35%)
│   [Hold to talk · Space]    │
│         🎙                  │
└─────────────────────────────┘
```

## Architecture

- **WinForms app** (.NET 8, Windows 10/11)
- **Embedded terminal**: Native conhost `copilot.exe` window reparented into the app via Win32 `SetParent`
- **Keystroke injection**: Voice commands are typed directly into the terminal using `PostMessage` + `WM_CHAR`
- **Azure OpenAI Realtime API**: Push-to-talk speech-to-text and text-to-speech
- **User Memory**: Persistent facts in `~/.copilot/user-memory.json` (MCP server)

No Node.js bridge, no xterm.js, no WebView2 — pure native Windows.

## License

See [LICENSE](LICENSE) for details.
