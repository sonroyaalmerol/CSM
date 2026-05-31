using System;
using ColossalFramework;
using CSM.API;
using CSM.API.Commands;
using CSM.Commands.Data.Internal;
using CSM.Networking;

namespace CSM.Sync
{
    /// <summary>
    ///     Monitors state hash mismatches and initiates recovery when
    ///     desync is detected. Counts consecutive mismatches and pauses
    ///     the simulation with a chat warning when the threshold is exceeded.
    ///
    ///     Recovery strategy:
    ///       1. Log the mismatch with tick and hash details.
    ///       2. Increment consecutive mismatch counter.
    ///       3. After MaxConsecutiveMismatches (3), pause the simulation
    ///          and notify all players via chat.
    ///       4. Require MinConsecutiveMatches (3) consecutive successful
    ///          hashes before auto-resuming. A single lucky match does not
    ///          reset the counter — the state must stabilize.
    ///       5. After MaxResyncThreshold (10), force a full world resync
    ///          by requesting a fresh world transfer from the server.
    /// </summary>
    public static class DesyncDetector
    {
        /// <summary>
        ///     Number of consecutive hash mismatches before pausing and warning.
        ///     At 1 hash/second, this is a 3-second sustained desync.
        /// </summary>
        private const int MaxConsecutiveMismatches = 3;

        /// <summary>
        ///     Number of consecutive mismatches before forcing a full resync
        ///     request. Set higher than MaxConsecutiveMismatches to give
        ///     players time to notice the warning before action is taken.
        /// </summary>
        private const int MaxResyncThreshold = 10;

        /// <summary>
        ///     Number of consecutive successful hash matches required before
        ///     the desync pause is lifted. Prevents a single lucky hash match
        ///     (e.g., both clients in the same wrong state for one tick) from
        ///     prematurely resuming.
        /// </summary>
        private const int MinConsecutiveMatches = 3;

        /// <summary>
        ///     Set to true once a resync has been triggered. Prevents
        ///     multiple resync requests from being sent simultaneously.
        /// </summary>
        private static bool _resyncRequested;

        private static int _consecutiveMismatches;
        private static int _consecutiveMatchesSincePause;
        private static bool _desyncPaused;
        private static uint _lastMismatchTick;

        /// <summary>True when the game is paused due to desync.</summary>
        public static bool IsDesyncPaused { get { return _desyncPaused; } }

        /// <summary>True when a resync is in progress.</summary>
        public static bool IsResyncing { get { return _resyncRequested; } }

        /// <summary>
        ///     Called when a STATE_HASH mismatch is detected.
        ///     Handles counting, pausing, and notification.
        /// </summary>
        public static void OnHashMismatch(uint tick, ulong ourHash, ulong theirHash, int senderId)
        {
            _consecutiveMismatches++;
            _consecutiveMatchesSincePause = 0;
            _lastMismatchTick = tick;

            if (_consecutiveMismatches >= MaxConsecutiveMismatches && !_desyncPaused)
            {
                _desyncPaused = true;

                // Pause the simulation
                ReflectionHelper.SetAttr(SimulationManager.instance, "m_simulationPaused", true);

                // Notify players
                string msg = $"Desync detected at tick {tick}. " +
                             $"Consecutive mismatches: {_consecutiveMismatches}. " +
                             $"Game paused. Hash: 0x{ourHash:X16} vs 0x{theirHash:X16}.";
                Log.Error($"[DesyncDetector] {msg}");
                PrintChatMessage(msg);
            }

            if (_consecutiveMismatches >= MaxResyncThreshold && !_resyncRequested)
            {
                RequestResync();
            }
        }

        /// <summary>
        ///     Called when a STATE_HASH matches successfully.
        ///     Only resumes after MinConsecutiveMatches consecutive matches
        ///     to ensure the state has truly stabilized.
        /// </summary>
        public static void OnHashMatch(uint tick)
        {
            if (_consecutiveMismatches > 0 && !_desyncPaused)
            {
                // Brief mismatch that self-corrected before pause threshold
                Log.Info($"[DesyncDetector] Hash match at tick {tick} after " +
                         $"{_consecutiveMismatches} mismatches. Self-corrected.");
                _consecutiveMismatches = 0;
                return;
            }

            if (_desyncPaused)
            {
                _consecutiveMatchesSincePause++;

                if (_consecutiveMatchesSincePause >= MinConsecutiveMatches)
                {
                    _consecutiveMismatches = 0;
                    _consecutiveMatchesSincePause = 0;
                    _desyncPaused = false;
                    ReflectionHelper.SetAttr(SimulationManager.instance, "m_simulationPaused", false);
                    Log.Info($"[DesyncDetector] Desync resolved after " +
                             $"{MinConsecutiveMatches} consecutive matches. Game resumed.");
                    PrintChatMessage("Desync resolved. Game resumed.");
                }
                else
                {
                    Log.Info($"[DesyncDetector] Hash match at tick {tick} " +
                             $"({_consecutiveMatchesSincePause}/{MinConsecutiveMatches} needed to resume).");
                }
                return;
            }

            // Not desync-paused, no recent mismatches — nothing to do
        }

        /// <summary>Reset all desync state. Called on disconnect.</summary>
        public static void Reset()
        {
            _consecutiveMismatches = 0;
            _consecutiveMatchesSincePause = 0;
            _desyncPaused = false;
            _resyncRequested = false;
            _lastMismatchTick = 0;
        }

        /// <summary>
        ///     Request a full world resync from the server.
        ///     This sends a RequestWorldTransferCommand to the server,
        ///     which triggers a fresh savegame transfer.
        ///     Only called on the client — the server never resyncs itself.
        /// </summary>
        private static void RequestResync()
        {
            if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Client)
            {
                _resyncRequested = true;
                string msg = "Desync threshold reached. Requesting full world resync from server...";
                Log.Warn($"[DesyncDetector] {msg}");
                PrintChatMessage(msg);

                try
                {
                    Command.SendToServer(new RequestWorldTransferCommand());
                }
                catch (Exception ex)
                {
                    Log.Error("[DesyncDetector] Failed to send resync request", ex);
                    _resyncRequested = false;
                }
            }
            else
            {
                Log.Warn("[DesyncDetector] Resync threshold reached on server. " +
                         "Server cannot resync itself. Clients must reconnect.");
                PrintChatMessage("Severe desync detected on server. Consider restarting the session.");
            }
        }

        private static void PrintChatMessage(string message)
        {
            try
            {
                var chat = Singleton<Chat>.instance;
                if (chat != null)
                {
                    chat.PrintGameMessage(Chat.MessageType.Warning, message);
                }
            }
            catch { }
        }
    }
}
