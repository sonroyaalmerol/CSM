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
        // Receive-side throttle: prevent executing terrain commands more
        // frequently than the sender could have sent them. This is a
        // defense-in-depth measure against relay amplification.
        private static float _lastHandleTime;
        private static int _lastSenderId = -1;
        private const float MinHandleInterval = 0.1f; // 100ms = match sender throttle

        public TerrainModificationHandler()
        {
            TransactionCmd = false;
            UseSequencedDelivery = true;
            RequiresTickSync = false;
        }
        protected override void Handle(TerrainModificationCommand command)
        {
            // Throttle: if the same sender sent a command too recently, skip.
            // This prevents relay amplification from causing extra brush applications.
            float now = Time.time;
            if (command.SenderId == _lastSenderId && now - _lastHandleTime < MinHandleInterval)
            {
                return;
            }
            _lastHandleTime = now;
            _lastSenderId = command.SenderId;

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
