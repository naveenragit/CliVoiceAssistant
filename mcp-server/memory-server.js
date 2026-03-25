import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { readFileSync, writeFileSync, mkdirSync, existsSync } from "fs";
import { join } from "path";
import { homedir } from "os";

// ── Configuration ────────────────────────────────────────────────────────────
const NARRATION_PORT = 9877;
const NARRATION_URL = `http://localhost:${NARRATION_PORT}/`;

// ── Memory file path ─────────────────────────────────────────────────────────
const MEMORY_DIR = join(homedir(), ".copilot");
const MEMORY_FILE = join(MEMORY_DIR, "user-memory.json");

function loadMemory() {
  try {
    if (!existsSync(MEMORY_FILE)) return {};
    return JSON.parse(readFileSync(MEMORY_FILE, "utf-8"));
  } catch {
    return {};
  }
}

function saveMemory(data) {
  if (!existsSync(MEMORY_DIR)) mkdirSync(MEMORY_DIR, { recursive: true });
  writeFileSync(MEMORY_FILE, JSON.stringify(data, null, 2), "utf-8");
}

// ── MCP Server ───────────────────────────────────────────────────────────────
const server = new Server(
  { name: "user-memory", version: "0.1.0" },
  { capabilities: { tools: {} } }
);

// ── Tool definitions ─────────────────────────────────────────────────────────
server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "remember_fact",
      description:
        "Store a persistent fact about the user (name, preferences, etc.). " +
        "Survives across sessions, conversation resets, and app restarts. " +
        "Shared between Copilot CLI and the Voice Assistant.",
      inputSchema: {
        type: "object",
        properties: {
          key: {
            type: "string",
            description:
              "Short label for the fact, e.g. 'name', 'preferred_language', 'team'.",
          },
          value: {
            type: "string",
            description:
              "The fact to remember, e.g. 'Naveen', 'Python', 'Platform Engineering'.",
          },
        },
        required: ["key", "value"],
      },
    },
    {
      name: "recall_facts",
      description:
        "Retrieve all persistent facts stored about the user. " +
        "Returns all key-value pairs from user memory.",
      inputSchema: {
        type: "object",
        properties: {},
      },
    },
    {
      name: "forget_fact",
      description:
        "Remove a persistent fact about the user by key.",
      inputSchema: {
        type: "object",
        properties: {
          key: {
            type: "string",
            description: "The key of the fact to remove.",
          },
        },
        required: ["key"],
      },
    },
    {
      name: "narrate",
      description:
        "Send text to the Voice Assistant so it can read it aloud to the user. " +
        "Use this after completing a task to give the user a brief spoken summary of what you did. " +
        "Keep narrations concise — 1-3 sentences, conversational tone, suitable for speech.",
      inputSchema: {
        type: "object",
        properties: {
          text: {
            type: "string",
            description:
              "The text to speak aloud. Should be a concise, spoken-friendly summary.",
          },
        },
        required: ["text"],
      },
    },
  ],
}));

// ── Tool execution ───────────────────────────────────────────────────────────
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  switch (name) {
    case "remember_fact": {
      const { key, value } = args;
      if (!key || !value) {
        return {
          content: [{ type: "text", text: "Error: both 'key' and 'value' are required." }],
          isError: true,
        };
      }
      const memory = loadMemory();
      memory[key] = value;
      saveMemory(memory);
      return {
        content: [
          {
            type: "text",
            text: `Remembered: ${key} = ${value} (${Object.keys(memory).length} total facts stored)`,
          },
        ],
      };
    }

    case "recall_facts": {
      const memory = loadMemory();
      const keys = Object.keys(memory);
      if (keys.length === 0) {
        return {
          content: [{ type: "text", text: "No facts stored yet." }],
        };
      }
      const lines = keys.map((k) => `• ${k}: ${memory[k]}`);
      return {
        content: [
          {
            type: "text",
            text: `User memory (${keys.length} facts):\n${lines.join("\n")}`,
          },
        ],
      };
    }

    case "forget_fact": {
      const { key } = args;
      if (!key) {
        return {
          content: [{ type: "text", text: "Error: 'key' is required." }],
          isError: true,
        };
      }
      const memory = loadMemory();
      if (!(key in memory)) {
        return {
          content: [{ type: "text", text: `No fact found with key '${key}'.` }],
        };
      }
      delete memory[key];
      saveMemory(memory);
      return {
        content: [
          { type: "text", text: `Forgot: '${key}' (${Object.keys(memory).length} facts remaining)` },
        ],
      };
    }

    case "narrate": {
      const { text } = args;
      if (!text) {
        return {
          content: [{ type: "text", text: "Error: 'text' is required." }],
          isError: true,
        };
      }
      try {
        const res = await fetch(NARRATION_URL, {
          method: "POST",
          headers: { "Content-Type": "text/plain" },
          body: text,
          signal: AbortSignal.timeout(5000),
        });
        if (res.ok) {
          return {
            content: [{ type: "text", text: `Narration sent to Voice Assistant (${text.length} chars).` }],
          };
        } else {
          return {
            content: [{ type: "text", text: `Voice Assistant returned HTTP ${res.status}. It may not be running.` }],
          };
        }
      } catch (err) {
        return {
          content: [{
            type: "text",
            text: `Could not reach Voice Assistant at ${NARRATION_URL}. Is it running? (${err.message})`,
          }],
        };
      }
    }

    default:
      return {
        content: [{ type: "text", text: `Unknown tool: ${name}` }],
        isError: true,
      };
  }
});

// ── Start ────────────────────────────────────────────────────────────────────
const transport = new StdioServerTransport();
await server.connect(transport);
