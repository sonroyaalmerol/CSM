using System.Collections.Generic;
using CSM.API.Commands;
using CSM.API.Helpers;
using CSM.BaseGame.Commands.Data.Terrain;
using CSM.BaseGame.Helpers;
using ColossalFramework;
using UnityEngine;

namespace CSM.BaseGame.Commands.Handler.Terrain
{
    public class TerrainModificationHandler : CommandHandler<TerrainModificationCommand>
    {
        // Per-sender receive-side throttle: prevent executing terrain commands
        // more frequently than the sender could have sent them. This prevents
        // relay amplification where a server broadcasting to N clients causes
        // N extra brush applications.
        private static readonly Dictionary<int, float> _lastHandleTimeBySender = new Dictionary<int, float>();
        private const float MinHandleInterval = 0.1f; // 100ms = match sender throttle
        private const int MaxTrackedSenders = 8; // Clean up stale entries

        public TerrainModificationHandler()
        {
            TransactionCmd = false;
            UseSequencedDelivery = true;
            RequiresTickSync = false;
        }
        protected override void Handle(TerrainModificationCommand command)
        {
            // Per-sender throttle: skip if this specific sender sent too recently.
            float now = Time.time;
            float lastTime;
            if (_lastHandleTimeBySender.TryGetValue(command.SenderId, out lastTime) &&
                now - lastTime < MinHandleInterval)
            {
                return;
            }
            _lastHandleTimeBySender[command.SenderId] = now;

            // Periodic cleanup of stale entries to prevent unbounded growth
            if (_lastHandleTimeBySender.Count > MaxTrackedSenders)
            {
                var keysToRemove = new List<int>();
                foreach (var kvp in _lastHandleTimeBySender)
                {
                    if (now - kvp.Value > 1.0f) // Older than 1 second
                        keysToRemove.Add(kvp.Key);
                }
                foreach (int key in keysToRemove)
                    _lastHandleTimeBySender.Remove(key);
            }

            TerrainTool tool = Singleton<ToolSimulator>.instance.GetTool<TerrainTool>(command.SenderId);

            // Apply data from command
            command.BrushData.CopyTo(ReflectionHelper.GetAttr<ToolController>(tool, "m_toolController").BrushData, 0);
            tool.m_brushSize = command.BrushSize;
            tool.m_strength = command.Strength;
            ReflectionHelper.SetAttr(tool, "m_mousePosition", command.MousePosition);
            ReflectionHelper.SetAttr(tool, "m_startPosition", command.StartPosition);
            ReflectionHelper.SetAttr(tool, "m_endPosition", command.EndPosition);
            ReflectionHelper.SetAttr(tool, "m_currentCost", 0);
            tool.m_mode = command.Mode;
            ReflectionHelper.SetAttr(tool, "m_mouseRightDown", command.MouseRightDown);

            IgnoreHelper.Instance.StartIgnore();
            // Call original method
            ReflectionHelper.Call(tool, "ApplyBrush");

            IgnoreHelper.Instance.EndIgnore();
        }
    }
}
