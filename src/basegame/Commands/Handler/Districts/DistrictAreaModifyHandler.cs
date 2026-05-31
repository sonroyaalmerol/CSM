using System.Collections.Generic;
using CSM.API.Commands;
using CSM.API.Helpers;
using CSM.BaseGame.Commands.Data.Districts;
using UnityEngine;

namespace CSM.BaseGame.Commands.Handler.Districts
{
    public class DistrictAreaModifyHandler : CommandHandler<DistrictAreaModifyCommand>
    {
        // Per-sender throttle: district brush painting fires on every mouse
        // move, same as terrain. Throttle to match sender rate and prevent
        // relay amplification.
        private static readonly Dictionary<int, float> _lastHandleTimeBySender = new Dictionary<int, float>();
        private const float MinHandleInterval = 0.1f; // 100ms
        private const int MaxTrackedSenders = 8;

        public DistrictAreaModifyHandler()
        {
            TransactionCmd = false;
            UseSequencedDelivery = true;
            RequiresTickSync = false;
        }

        protected override void Handle(DistrictAreaModifyCommand command)
        {
            float now = Time.time;
            float lastTime;
            if (_lastHandleTimeBySender.TryGetValue(command.SenderId, out lastTime) &&
                now - lastTime < MinHandleInterval)
            {
                return;
            }
            _lastHandleTimeBySender[command.SenderId] = now;

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

            DistrictTool.ApplyBrush(command.Layer, command.District, command.BrushRadius, command.StartPosition, command.EndPosition);
            DistrictManager.instance.NamesModified();
            DistrictManager.instance.ParkNamesModified();

            IgnoreHelper.Instance.EndIgnore();
        }
    }
}