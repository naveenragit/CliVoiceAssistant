---
name: remember
description: Store or recall persistent facts about the user (name, preferences, etc.) that survive across sessions and conversation resets.
---

# User Memory Skill

Manage persistent user memory stored at `~/.copilot/user-memory.json`.

## Commands

### Remember a fact
When the user says "remember that my name is X" or "remember I prefer Y":
1. Extract the key (e.g., "name", "preferred_language") and value
2. Use the `remember_fact` MCP tool to persist it
3. Confirm what was saved

### Recall facts
When the user asks "what do you know about me?" or "what's my name?":
1. Use the `recall_facts` MCP tool to retrieve all stored facts
2. Present them clearly

### Forget a fact
When the user says "forget my X" or "remove that":
1. Use the `forget_fact` MCP tool to delete it
2. Confirm the removal

## Storage

Facts are stored as key-value pairs in `~/.copilot/user-memory.json`:
```json
{
  "name": "Naveen",
  "preferred_language": "C#",
  "team": "Platform Engineering"
}
```

This file is read by both Copilot CLI (via this skill/MCP server) and the Voice Assistant (injected into the Realtime API system prompt on connection).
