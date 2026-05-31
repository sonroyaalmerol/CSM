using System.Collections.Generic;
using CSM.API.Commands;
using CSM.API.Helpers;
using CSM.BaseGame.Commands.Data.Terrain;
using UnityEngine;

namespace CSM.BaseGame.Commands.Handler.Terrain
{
    public class SoilTradeHandler : CommandHandler<SoilTradeCommand>
    {
        // Per-sender receive-side throttle: match sender's 150ms interval.
        // Soil trade is idempotent (only the latest DirtBuffer matters)
        // so skipping rapid successive updates is safe.
        private static readonly Dictionary<int, float> _lastHandleTimeBySender = new Dictionary<int, float>();
        private const float MinHandleInterval = 0.15f;
        private const int MaxTrackedSenders = 8;

        public SoilTradeHandler()
        {
            TransactionCmd = false;
            UseSequencedDelivery = true;
            RequiresTickSync = false;
        }
        protected override void Handle(SoilTradeCommand command)
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

            // Periodic cleanup of stale entries
            if (_lastHandleTimeBySender.Count > MaxTrackedSenders)
            {
                var keysToRemove = new List<int>();
                foreach (var kvp in _lastHandleTimeBySender)
                {
                    if (now - kvp.Value > 1.0f)
                        keysToRemove.Add(kvp.Key);
                }
                foreach (int key in keysToRemove)
                    _lastHandleTimeBySender.Remove(key);
            }

            IgnoreHelper.Instance.StartIgnore();

            TerrainManager.instance.DirtBuffer = command.DirtBuffer;

            IgnoreHelper.Instance.EndIgnore();
        }
    }
}
