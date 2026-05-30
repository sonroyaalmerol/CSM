using System;
using ColossalFramework;
using CSM.API;
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
    ///       4. On the next successful match, reset the counter and resume.
    ///       5. Optionally force a resync after MaxResyncThreshold (10).
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

        private static int _consecutiveMismatches;
        private static bool _desyncPaused;
        private static uint _lastMismatchTick;

        /// <summary>True when the game is paused due to desync.</summary>
        public static bool IsDesyncPaused { get { return _desyncPaused; } }

        /// <summary>
        ///     Called when a STATE_HASH mismatch is detected.
        ///     Handles counting, pausing, and notification.
        /// </summary>
        public static void OnHashMismatch(uint tick, ulong ourHash, ulong theirHash, int senderId)
        {
            _consecutiveMismatches++;
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

            if (_consecutiveMismatches >= MaxResyncThreshold)
            {
                Log.Warn($"[DesyncDetector] Resync threshold reached ({_consecutiveMismatches}). " +
                         $"A full resync would be needed but is not yet implemented.");
                // Future: trigger a full world resync request here
            }
        }

        /// <summary>
        ///     Called when a STATE_HASH matches successfully.
        ///     Resets the mismatch counter and unpauses if was desync-paused.
        /// </summary>
        public static void OnHashMatch(uint tick)
        {
            if (_consecutiveMismatches > 0)
            {
                Log.Info($"[DesyncDetector] Hash match at tick {tick} after " +
                         $"{_consecutiveMismatches} mismatches. Resynced.");
            }

            _consecutiveMismatches = 0;

            if (_desyncPaused)
            {
                _desyncPaused = false;
                ReflectionHelper.SetAttr(SimulationManager.instance, "m_simulationPaused", false);
                PrintChatMessage("Desync resolved. Game resumed.");
            }
        }

        /// <summary>Reset all desync state. Called on disconnect.</summary>
        public static void Reset()
        {
            _consecutiveMismatches = 0;
            _desyncPaused = false;
            _lastMismatchTick = 0;
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
