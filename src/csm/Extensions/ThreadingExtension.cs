using System;
using ColossalFramework;
using CSM.API;
using CSM.API.Commands;
using CSM.API.Helpers;
using CSM.API.Networking.Status;
using CSM.BaseGame.Injections;
using CSM.Commands;
using CSM.Helpers;
using CSM.Networking;
using CSM.Sync;
using ICities;
using LiteNetLib;

namespace CSM.Extensions
{
    public class ThreadingExtension : ThreadingExtensionBase
    {
        private DateTime _lastEconomyAndDropSync;
        private DateTime _lastTickSync;
        private uint _lastHashTick;

        // Tick sync interval: send TICK_SYNC every 200ms (12 ticks at 60fps)
        private const int TickSyncIntervalMs = 200;

        private static int GetLatencyAwareSyncIntervalMs()
        {
            long maxLatency = 0;
            if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Client)
            {
                maxLatency = MultiplayerManager.Instance.CurrentClient.ClientPlayer.Latency;
            }
            else if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server &&
                     MultiplayerManager.Instance.CurrentServer.ConnectedPlayers.Count > 0)
            {
                foreach (var player in MultiplayerManager.Instance.CurrentServer.ConnectedPlayers.Values)
                {
                    if (player.Latency > maxLatency)
                        maxLatency = player.Latency;
                }
            }

            // Scale sync interval: faster sync at higher latency, clamped to [500ms, 2000ms]
            return (int)Math.Max(500, Math.Min(2000, maxLatency * 3));
        }

        public override void OnCreated(IThreading threading)
        {
            Singleton<MainThreadTracker>.Ensure();
        }

        public override void OnBeforeSimulationTick()
        {
            base.OnBeforeSimulationTick();

            // Normally, the game is paused when the player is in Esc or similar menus. We ignore this setting.
            if (SimulationManager.instance.ForcedSimulationPaused && MultiplayerManager.Instance.CurrentRole != MultiplayerRole.None)
            {
                SimulationManager.instance.ForcedSimulationPaused = false;
            }

            // Process changes in the pause state and game speed
            SpeedPauseHelper.SimulationStep();

            // Don't process events while the client is loading the level.
            // It first needs to be processed in loading extension to make sure the level is fully loaded.
            if (MultiplayerManager.Instance.CurrentRole != MultiplayerRole.Client ||
                (MultiplayerManager.Instance.CurrentClient.Status == ClientStatus.Connected ||
                 MultiplayerManager.Instance.CurrentClient.Status == ClientStatus.Downloading))
            {
                // Process events of the network lib
                MultiplayerManager.Instance.ProcessEvents();
            }

            // ── Frame gate ─────────────────────────────────
            // If tick-sync is active and the frame gate says we can't advance,
            // skip this simulation tick by re-pausing.
            if (TickClock.IsInitialized && !FrameGate.CanAdvance())
            {
                // Signal the tick loop to skip this frame
                ReflectionHelper.SetAttr(SimulationManager.instance, "m_simulationPaused", true);
            }
        }

        public override void OnAfterSimulationTick()
        {
            // ── Execute buffered tick-synced commands ──────
            // Execute all commands that were buffered for the tick we just completed.
            if (TickClock.IsInitialized)
            {
                FrameGate.ExecuteTick(TickClock.LocalTick);

                // ── Periodic tick sync (server broadcasts tick position) ──
                if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server)
                {
                    if (DateTime.Now.Subtract(_lastTickSync).TotalMilliseconds > TickSyncIntervalMs)
                    {
                        SendTickSync();
                        _lastTickSync = DateTime.Now;
                    }
                }

                // ── Periodic state hash verification ────────
                if (StateHasher.ShouldHash(TickClock.LocalTick) && TickClock.LocalTick != _lastHashTick)
                {
                    _lastHashTick = TickClock.LocalTick;
                    SendStateHash();
                }
            }

            // Send economy and frame drop packets based on current latency
            if (DateTime.Now.Subtract(_lastEconomyAndDropSync).TotalMilliseconds > GetLatencyAwareSyncIntervalMs())
            {
                // Only send economy and dropped frames when connected
                // (loading may accumulate dropped frames we need to ignore)
                if (MultiplayerManager.Instance.IsConnected())
                {
                    ResourceCommandHandler.Send();
                    SlowdownHelper.SendDroppedFrames();
                }
                _lastEconomyAndDropSync = DateTime.Now;
            }

            // Finish transactions
            TransactionHandler.FinishSend();

            // Check if ignore helper is still in ignore state at the end of the simulation step
            if (IgnoreHelper.Instance.IsIgnored())
            {
                Log.Warn("Ignore helper not stopped at end of simulation tick. A restart may be required.");
                Chat.Instance.PrintGameMessage(Chat.MessageType.Warning, "Warning: Synchronization problem detected. If you encounter problems, restart the multiplayer session!");
                IgnoreHelper.Instance.ResetIgnore();
            }
        }

        // ── Tick sync broadcast ────────────────────────────

        private void SendTickSync()
        {
            uint depth = TickClock.CalculatePipelineDepth();
            byte[] packet = SyncBatch.BuildTickSync(TickClock.LocalTick, depth);

            foreach (var player in MultiplayerManager.Instance.CurrentServer.ConnectedPlayers.Values)
            {
                if (player is CSMPlayer csmPlayer && csmPlayer.NetPeer != null)
                {
                    csmPlayer.NetPeer.Send(packet, LiteNetLib.DeliveryMethod.ReliableSequenced);
                }
            }
        }

        // ── State hash broadcast ───────────────────────────

        private void SendStateHash()
        {
            ulong hash = StateHasher.ComputeHash();
            int senderId = -1;
            if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Client)
            {
                senderId = MultiplayerManager.Instance.CurrentClient.ClientId;
            }

            byte[] packet = SyncBatch.BuildStateHash(TickClock.LocalTick, hash, senderId);

            if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server)
            {
                // Server sends to all clients
                foreach (var player in MultiplayerManager.Instance.CurrentServer.ConnectedPlayers.Values)
                {
                    if (player is CSMPlayer csmPlayer && csmPlayer.NetPeer != null)
                    {
                        csmPlayer.NetPeer.Send(packet, LiteNetLib.DeliveryMethod.Unreliable);
                    }
                }
            }
            else
            {
                // Client sends to server
                var serverPeer = MultiplayerManager.Instance.CurrentClient.ServerPeer;
                if (serverPeer != null)
                {
                    serverPeer.Send(packet, LiteNetLib.DeliveryMethod.Unreliable);
                }
            }
        }
    }

    class MainThreadTracker : Singleton<MainThreadTracker>
    {
        public void LateUpdate()
        {
            // Check if ignore helper is in ignore state outside of other Update functions
            if (IgnoreHelper.Instance.IsIgnored())
            {
                Log.Warn("Ignore helper not stopped at arbitrary point of UI tick. A restart may be required.");
                Chat.Instance.PrintGameMessage(Chat.MessageType.Warning, "Warning: Synchronization problem detected. If you encounter problems, restart the multiplayer session!");
                IgnoreHelper.Instance.ResetIgnore();
            }
        }
    }
}
