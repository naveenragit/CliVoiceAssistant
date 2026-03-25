# Voice Assistant v4 — Copilot CLI Plugin

A Windows Forms voice assistant powered by **Azure OpenAI `gpt-4o-realtime-preview`**.
Speak commands, see them typed into an embedded Copilot CLI terminal, and hear responses read aloud.

## Plugin Installation

```bash
copilot plugin install ./voice-assistant-v4
```

Then launch from any Copilot CLI session:
```
/voice
```

## Architecture

- **WinForms app** (.NET 8, Windows 10/11)
- **Embedded terminal**: Native conhost `copilot.exe` window reparented into the app via Win32 `SetParent`
- **Keystroke injection**: Voice commands are typed directly into the terminal using `PostMessage` + `WM_CHAR`
- **Azure OpenAI Realtime API**: Push-to-talk speech-to-text and text-to-speech
- **User Memory**: Persistent facts in `~/.copilot/user-memory.json` (MCP server)

No Node.js bridge, no xterm.js, no WebView2 — pure native Windows.

## Quick Start (Development)

```bash
cd voice-assistant-v4
dotnet run
```

Or build and run the exe directly:
```bash
dotnet build
bin/Debug/net8.0-windows10.0.17763.0/VoiceAssistant.exe
```

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

## Prerequisites

- .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`)
- Windows 10/11
- Azure OpenAI resource with `gpt-4o-realtime-preview` deployment
- Microphone + speakers
