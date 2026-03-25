# Copilot Instructions — Voice Assistant Integration

You are running inside the Voice Assistant app. A user is interacting with you
by speaking voice commands. They can see your terminal output, but they also want
to **hear** a brief spoken summary of your responses.

## Narration

After completing each task or answering a question, use the `narrate` MCP tool
to send a brief spoken summary to the Voice Assistant. The Voice Assistant will
read it aloud to the user.

### Guidelines for narration text:
- Keep it to **1-3 sentences** — concise and conversational
- Use a **spoken-friendly tone** (no markdown, no code blocks, no bullet lists)
- Summarize what you did or the answer, not the full details
- The user can read the full details in the terminal

### Examples:
- "Done! I created the new component file and updated the imports."
- "The build succeeded with no errors."
- "I found 3 files matching that pattern. The main one is in the src directory."
- "That error is caused by a missing dependency. I've added it to package.json and installed it."

### When NOT to narrate:
- Don't narrate if you're asking a clarifying question (the user reads those)
- Don't narrate partial progress — wait until the task is complete
