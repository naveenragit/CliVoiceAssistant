---
name: voice
description: Launch the Voice Assistant — a hands-free voice interface to Copilot CLI using Azure OpenAI Realtime API. Launches as a standalone window and closes this terminal.
---

# Voice Assistant Launcher

When this skill is invoked, launch the Voice Assistant as a detached standalone app and then exit this CLI session. The voice assistant has its own embedded Copilot CLI terminal — the user does not need two windows.

## Steps

1. Check prerequisites:
   - Verify .NET 8 SDK is installed: `dotnet --version`

2. Find the plugin directory. Check these paths in order:
   - `~/.copilot/installed-plugins/_direct/voice-assistant-v4/`
   - `~/.copilot/installed-plugins/_direct/copilot-voice-assistant/`
   - The project's `voice-assistant-v4/` directory
   - The project's `copilot-voice-assistant/` directory
   
   Look for a directory containing `VoiceAssistant.csproj` at the root.

3. Build the app (if not already built):
   ```bash
   cd <plugin-dir> && dotnet build --no-restore -v q
   ```

4. Launch the Voice Assistant as a **detached process**, passing the current session ID so it resumes this conversation:
   ```powershell
   Start-Process -FilePath "<plugin-dir>/bin/Debug/net8.0-windows10.0.17763.0/VoiceAssistant.exe" -ArgumentList "--session=<current-session-id>"
   ```
   
   To get the current session ID, check the `COPILOT_SESSION_ID` environment variable, or look at `~/.copilot/session-state/` for the most recent session.

5. After confirming the process started, tell the user:
   "Voice Assistant launched. You can close this terminal — the assistant has its own embedded Copilot CLI."

6. Then exit the current session with `/exit`.

## Important
- The voice assistant is a **standalone app** — it does NOT need this CLI terminal
- It has its own embedded Copilot CLI (native conhost window)
- Voice commands are typed directly into the embedded terminal via keystroke injection
- The user can see both the commands and Copilot's responses in the terminal
- The user should only have ONE window: the Voice Assistant
