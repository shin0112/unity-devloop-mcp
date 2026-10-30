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
            if (UnityDevloopMcpBridge.IsRunning)
                Debug.Log($"[UnityDevloopMcp] Bridge running on http://127.0.0.1:{UnityDevloopMcpBridge.ActivePort}/ (base port {UnityDevloopMcpBridge.Port})");
            else
                Debug.Log($"[UnityDevloopMcp] Bridge stopped (base port {UnityDevloopMcpBridge.Port})");
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
