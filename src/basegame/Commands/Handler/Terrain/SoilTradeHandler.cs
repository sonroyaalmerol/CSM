using CSM.API.Commands;
using CSM.API.Helpers;
using CSM.BaseGame.Commands.Data.Terrain;
using UnityEngine;

namespace CSM.BaseGame.Commands.Handler.Terrain
{
    public class SoilTradeHandler : CommandHandler<SoilTradeCommand>
    {
        // Receive-side throttle: match sender's 150ms interval
        private static float _lastHandleTime;
        private const float MinHandleInterval = 0.15f;

        public SoilTradeHandler()
        {
            TransactionCmd = false;
            UseSequencedDelivery = true;
            RequiresTickSync = false;
        }
        protected override void Handle(SoilTradeCommand command)
        {
            // Throttle: skip if received too quickly from the network
            float now = Time.time;
            if (now - _lastHandleTime < MinHandleInterval)
            {
                return;
            }
            _lastHandleTime = now;

            IgnoreHelper.Instance.StartIgnore();

            TerrainManager.instance.DirtBuffer = command.DirtBuffer;

            IgnoreHelper.Instance.EndIgnore();
        }
    }
}
