using ColossalFramework;
using CSM.API.Commands;
using CSM.API.Helpers;
using CSM.BaseGame.Commands.Data.Terrain;
using HarmonyLib;
using UnityEngine;

namespace CSM.BaseGame.Injections
{
    [HarmonyPatch(typeof(TerrainTool))]
    [HarmonyPatch("ApplyBrush")]
    public class ApplyTerrainBrush
    {
        // Throttle terrain modification sends to avoid flooding the network
        // with commands every frame during terraforming.
        private static float _lastSendTime;
        private static TerrainModificationCommand _lastSent;

        // Minimum interval between terrain modification commands (in seconds).
        // At 300ms latency, we batch roughly one update per round-trip.
        private const float MinSendInterval = 0.1f; // 100ms = 10 commands/sec

        public static void Prefix()
        {
            TerrainTool tool = ToolsModifierControl.GetTool<TerrainTool>();
            if (!IgnoreHelper.Instance.IsIgnored() && ReflectionHelper.GetAttr<ToolBase.ToolErrors>(tool, "m_toolErrors") == ToolBase.ToolErrors.None)
            {
                float now = Time.time;

                // Always allow the first command through
                if (_lastSent != null && now - _lastSendTime < MinSendInterval)
                {
                    // Throttled: skip sending this frame but update the pending state
                    // so the next send carries the latest positions.
                    _lastSent.StartPosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_startPosition");
                    _lastSent.EndPosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_endPosition");
                    _lastSent.MousePosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_mousePosition");
                    return;
                }

                _lastSendTime = now;

                _lastSent = new TerrainModificationCommand
                {
                    BrushData = Singleton<ToolController>.instance.BrushData,
                    BrushSize = tool.m_brushSize,
                    StartPosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_startPosition"),
                    EndPosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_endPosition"),
                    MousePosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_mousePosition"),
                    Mode = tool.m_mode,
                    Strength = tool.m_strength,
                    MouseRightDown = ReflectionHelper.GetAttr<bool>(tool, "m_mouseRightDown")
                };

                Command.SendToAll(_lastSent);
            }
        }
    }

    [HarmonyPatch(typeof(TerrainManager))]
    [HarmonyPatch("DirtBuffer", MethodType.Setter)]
    public class SoilChanged
    {
        private static float _lastSendTime;
        private static int _pendingDirtBuffer;

        // Throttle soil trade commands similarly
        private const float MinSendInterval = 0.15f; // 150ms

        public static void Postfix(int ___m_dirtBuffer)
        {
            if (IgnoreHelper.Instance.IsIgnored())
            {
                return;
            }

            float now = Time.time;
            _pendingDirtBuffer = ___m_dirtBuffer;

            if (now - _lastSendTime < MinSendInterval)
            {
                return;
            }

            _lastSendTime = now;

            Command.SendToAll(new SoilTradeCommand
            {
                DirtBuffer = _pendingDirtBuffer
            });
        }
    }
}
