import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { readFileSync, writeFileSync, mkdirSync, existsSync } from "fs";
import { join } from "path";
import { homedir } from "os";

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
