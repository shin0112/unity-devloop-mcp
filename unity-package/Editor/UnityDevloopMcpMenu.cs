using UnityEditor;
using UnityEngine;

namespace Shin0112.UnityDevloopMcp.Editor
{
    public static class UnityDevloopMcpMenu
    {
        private const string MenuRoot = "Tools/Unity Devloop MCP/";

        [MenuItem(MenuRoot + "Status")]
        private static void ShowStatus()
        {
            string state = UnityDevloopMcpBridge.IsRunning ? "running" : "stopped";
            Debug.Log($"[UnityDevloopMcp] Bridge {state} on http://127.0.0.1:{UnityDevloopMcpBridge.Port}/");
        }

        [MenuItem(MenuRoot + "Enable Bridge")]
        private static void EnableBridge()
        {
            EditorPrefs.SetBool(UnityDevloopMcpBridge.EnabledPrefKey, true);
            UnityDevloopMcpBridge.Start();
            ShowStatus();
        }

        [MenuItem(MenuRoot + "Disable Bridge")]
        private static void DisableBridge()
        {
            EditorPrefs.SetBool(UnityDevloopMcpBridge.EnabledPrefKey, false);
            UnityDevloopMcpBridge.Stop();
            ShowStatus();
        }
    }
}
