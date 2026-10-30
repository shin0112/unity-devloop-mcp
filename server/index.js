#!/usr/bin/env node
import path from "node:path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const basePort = Number(process.env.UNITY_DEVLOOP_PORT ?? 17890);
const portRange = Number(process.env.UNITY_DEVLOOP_PORT_RANGE ?? 10);
const timeoutMs = Number(process.env.UNITY_DEVLOOP_TIMEOUT_MS ?? 35000);
const pingTimeoutMs = Number(process.env.UNITY_DEVLOOP_PING_TIMEOUT_MS ?? 1500);
const projectPath = process.env.UNITY_DEVLOOP_PROJECT ?? process.cwd();

let activePort = null;
let lastScan = [];

function normalizePath(value) {
  return path.resolve(value).replace(/[\\/]+$/, "").toLowerCase();
}

function isSameOrParent(projectRoot, candidate) {
  const root = normalizePath(projectRoot);
  const current = normalizePath(candidate);
  return current === root || current.startsWith(root + path.sep);
}

async function post(port, route, body, timeout) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  try {
    const response = await fetch(`http://127.0.0.1:${port}${route}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {}),
      signal: controller.signal,
    });
    return await response.text();
  } finally {
    clearTimeout(timer);
  }
}

async function ping(port) {
  try {
    const info = JSON.parse(await post(port, "/ping", {}, pingTimeoutMs));
    return info.ok ? { port, projectPath: info.projectPath, pid: info.pid } : null;
  } catch {
    return null;
  }
}

async function resolvePort() {
  const ports = Array.from({ length: portRange }, (_, i) => basePort + i);
  lastScan = (await Promise.all(ports.map(ping))).filter(Boolean);

  const match = lastScan.find((info) => info.projectPath && isSameOrParent(info.projectPath, projectPath));
  if (match) return match.port;
  return lastScan.length === 1 ? lastScan[0].port : null;
}

function notReachable(reason) {
  const found = lastScan.length
    ? lastScan.map((info) => `${info.port} → ${info.projectPath} (pid ${info.pid})`).join(", ")
    : "none";
  return JSON.stringify({
    ok: false,
    error:
      `Unity Editor bridge not reachable (${reason}). Scanned ports ${basePort}-${basePort + portRange - 1} for project '${projectPath}'; ` +
      `bridges found: ${found}. Make sure the editor is open, the bridge package is installed, and scripts are not reloading.`,
  });
}

const retryableRoutes = new Set(["/status", "/console"]);

async function callUnity(route, body) {
  const maxAttempts = retryableRoutes.has(route) ? 2 : 1;
  if (maxAttempts === 1 && activePort === null) activePort = await resolvePort();

  for (let attempt = 0; attempt < maxAttempts; attempt++) {
    if (activePort === null || attempt > 0) activePort = await resolvePort();
    if (activePort === null) return notReachable("no matching bridge answered /ping");

    try {
      return await post(activePort, route, body, timeoutMs);
    } catch (error) {
      const failedPort = activePort;
      activePort = null;
      if (attempt + 1 >= maxAttempts) return notReachable(`port ${failedPort}: ${error.message}`);
    }
  }

  return notReachable("unknown");
}

function asText(text) {
  return { content: [{ type: "text", text }] };
}

const server = new McpServer({ name: "unity-devloop-mcp", version: "0.2.0" });

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
  async ({ path: menuPath }) => asText(await callUnity("/menu", { path: menuPath })),
);

server.tool(
  "unity_refresh",
  "Run AssetDatabase.Refresh so file changes made outside the editor are imported and scripts recompile.",
  {},
  async () => asText(await callUnity("/refresh")),
);

await server.connect(new StdioServerTransport());
