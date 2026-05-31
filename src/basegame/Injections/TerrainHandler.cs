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

        // Minimum interval between terrain modification commands (in seconds).
        // At 300ms latency, we batch roughly one update per round-trip.
        private const float MinSendInterval = 0.1f; // 100ms = 10 commands/sec

        public static void Prefix()
        {
            TerrainTool tool = ToolsModifierControl.GetTool<TerrainTool>();
            if (!IgnoreHelper.Instance.IsIgnored() && ReflectionHelper.GetAttr<ToolBase.ToolErrors>(tool, "m_toolErrors") == ToolBase.ToolErrors.None)
            {
                float now = Time.time;

                // Throttle: skip sending if the interval hasn't elapsed since last send.
                if (now - _lastSendTime < MinSendInterval)
                {
                    return;
                }

                _lastSendTime = now;

                Command.SendToAll(new TerrainModificationCommand
                {
                    BrushData = Singleton<ToolController>.instance.BrushData,
                    BrushSize = tool.m_brushSize,
                    StartPosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_startPosition"),
                    EndPosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_endPosition"),
                    MousePosition = ReflectionHelper.GetAttr<Vector3>(tool, "m_mousePosition"),
                    Mode = tool.m_mode,
                    Strength = tool.m_strength,
                    MouseRightDown = ReflectionHelper.GetAttr<bool>(tool, "m_mouseRightDown")
                });
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
