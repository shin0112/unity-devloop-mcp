# unity-devloop-mcp

MCP server for the Unity **edit → compile → check** loop. An AI client (Claude Code, etc.) can read the editor state and console/compiler errors, trigger an asset refresh after editing scripts outside Unity, and run menu items — without you copying console output by hand.

Two parts:

| Part | Path | Role |
| --- | --- | --- |
| MCP server (Node, stdio) | `server/` | Exposes the tools below and forwards calls to the editor |
| Unity bridge (UPM, Editor-only) | `unity-package/` | Local HTTP listener on `127.0.0.1` that runs requests on the editor main thread |

The bridge listens only on `127.0.0.1` and only while the editor is open. Nothing is included in player builds.

## Tools

| Tool | Input | Result |
| --- | --- | --- |
| `unity_status` | — | project path, Unity version, active scene, build target, `isCompiling`, `isPlaying`, `isPaused` |
| `unity_console` | `limit` (default 100), `types` (`log,warning,error,exception,assert,compile-error,compile-warning`), `clear` | recent entries captured by the bridge (stack traces kept for errors/exceptions) |
| `unity_refresh` | — | runs `AssetDatabase.Refresh` (imports external file changes, starts recompilation) |
| `unity_execute_menu` | `path` | runs `EditorApplication.ExecuteMenuItem(path)` |

Console capture starts when the bridge loads, so entries from before the last script reload are not kept. Compiler errors are captured from `CompilationPipeline` and survive a failed compile (no reload happens on failure).

## Install

### 1. Unity package

Add to `Packages/manifest.json`:

```json
"com.shin0112.unity-devloop-mcp": "https://github.com/shin0112/unity-devloop-mcp.git?path=/unity-package"
```

or, for a local clone:

```json
"com.shin0112.unity-devloop-mcp": "file:C:/projects/unity-devloop-mcp/unity-package"
```

Menu: `Tools > Unity Devloop MCP > Status / Enable Bridge / Disable Bridge`.

### 2. MCP server (Claude Code)

From GitHub:

```bash
claude mcp add unity-devloop -- npx -y github:shin0112/unity-devloop-mcp
```

From a local clone:

```bash
cd C:/projects/unity-devloop-mcp && npm install
claude mcp add unity-devloop -- node C:/projects/unity-devloop-mcp/server/index.js
```

## Configuration

| Setting | Where | Default |
| --- | --- | --- |
| Port | EditorPrefs `Shin0112.UnityDevloopMcp.Port` (editor) and env `UNITY_DEVLOOP_PORT` (server) — keep them equal | `17890` |
| Request timeout | env `UNITY_DEVLOOP_TIMEOUT_MS` (server); the bridge waits up to 30 s for the main thread | `35000` |

## Notes

- Requests run on the editor main thread via `EditorApplication.update`. If the editor is unfocused and throttles updates, calls may be slow or time out.
- During a script reload the bridge restarts; calls fail briefly with "not reachable".
- `unity_execute_menu` can run any menu item. Only connect clients you trust.

## License

MIT
