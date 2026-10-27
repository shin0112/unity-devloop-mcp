#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const port = Number(process.env.UNITY_DEVLOOP_PORT ?? 17890);
const timeoutMs = Number(process.env.UNITY_DEVLOOP_TIMEOUT_MS ?? 35000);
const baseUrl = `http://127.0.0.1:${port}`;

async function callUnity(route, body) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(baseUrl + route, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {}),
      signal: controller.signal,
    });
    return await response.text();
  } catch (error) {
    return JSON.stringify({
      ok: false,
      error: `Unity Editor not reachable at ${baseUrl} (${error.message}). Make sure the editor is open, the bridge package is installed, and scripts are not reloading.`,
    });
  } finally {
    clearTimeout(timer);
  }
}

function asText(text) {
  return { content: [{ type: "text", text }] };
}

const server = new McpServer({ name: "unity-devloop-mcp", version: "0.1.0" });

server.tool(
  "unity_status",
  "Editor state: project path, Unity version, compiling, play mode, active scene, build target.",
  {},
  async () => asText(await callUnity("/status")),
);

server.tool(
  "unity_console",
  "Recent Unity console entries captured by the bridge (logs, warnings, errors, exceptions, compiler messages). Entries before the last script reload are not kept.",
  {
    limit: z.number().int().min(1).max(500).optional().describe("Max entries, newest last. Default 100."),
    types: z.string().optional().describe("Comma-separated filter: log,warning,error,exception,assert,compile-error,compile-warning. Default all."),
    clear: z.boolean().optional().describe("Clear the bridge buffer after reading."),
  },
  async (args) => asText(await callUnity("/console", args)),
);

server.tool(
  "unity_execute_menu",
  "Execute a Unity Editor menu item by path, e.g. 'Tools/Profiler/Export Capture Stats'.",
  { path: z.string().min(1) },
  async ({ path }) => asText(await callUnity("/menu", { path })),
);

server.tool(
  "unity_refresh",
  "Run AssetDatabase.Refresh so file changes made outside the editor are imported and scripts recompile.",
  {},
  async () => asText(await callUnity("/refresh")),
);

await server.connect(new StdioServerTransport());
