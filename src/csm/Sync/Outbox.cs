using CSM.API;
using CSM.API.Commands;
using CSM.Networking;
using LiteNetLib;

namespace CSM.Sync
{
    /// <summary>
    ///     Redundant command retransmission system inspired by GGPO.
    ///
    ///     When tick-synced commands are sent, they are also stored in a
    ///     history ring buffer. On each TICK_SYNC interval, the most recent
    ///     commands (last ~400ms) are re-sent. This makes the system robust
    ///     against single packet loss: the next interval's retransmission
    ///     delivers the dropped command.
    ///
    ///     The receiver deduplicates by checking sender ID + command type
    ///     in the command buffer, so redundant sends are harmless — they're
    ///     silently ignored if the original arrived.
    ///
    ///     Only tick-synced commands (RequiresTickSync = true) are tracked.
    ///     Non-tick-synced commands use reliable ordered delivery which
    ///     already guarantees delivery (at the cost of head-of-line blocking).
    ///
    ///     Thread safety: all methods are called from the main thread.
    /// </summary>
    public static class Outbox
    {
        /// <summary>
        ///     Total history ring size. Must be large enough to hold all
        ///     commands from the retransmission window.
        /// </summary>
        private const int HistorySize = 32;

        /// <summary>
        ///     Number of recent entries to retransmit on each flush.
        ///     At 200ms TICK_SYNC intervals with ~5 commands/sec,
        ///     10 entries covers ~2 intervals (400ms). This provides
        ///     redundancy for 1-2 lost packets without excessive bandwidth.
        /// </summary>
        private const int RedundantSendCount = 10;

        /// <summary>
        ///     Ring buffer of recently sent commands for redundant retransmission.
        ///     Older entries are overwritten as new ones arrive.
        /// </summary>
        private static readonly HistoryEntry[] _history = new HistoryEntry[HistorySize];

        private static int _historyHead;
        private static int _historyCount;

        /// <summary>
        ///     A serialized command tracked for redundant retransmission.
        /// </summary>
        public struct HistoryEntry
        {
            /// <summary>Serialized protobuf bytes, ready to re-send.</summary>
            public byte[] Data;

            /// <summary>The command type name for logging.</summary>
            public string TypeName;

            /// <summary>Target tick for deduplication at receiver.</summary>
            public uint TargetTick;

            /// <summary>Sender ID for deduplication at receiver.</summary>
            public int SenderId;
        }

        /// <summary>
        ///     Record a tick-synced command that was just sent.
        ///     Stores the serialized bytes in the history ring for
        ///     redundant retransmission on the next TICK_SYNC interval.
        /// </summary>
        /// <param name="cmd">The command (for metadata extraction).</param>
        /// <param name="serializedData">Pre-serialized bytes to avoid double serialization.</param>
        public static void RecordSent(CommandBase cmd, byte[] serializedData)
        {
            _history[_historyHead] = new HistoryEntry
            {
                Data = serializedData,
                TypeName = cmd.GetType().Name,
                TargetTick = cmd.TargetFrameIndex,
                SenderId = cmd.SenderId
            };

            _historyHead = (_historyHead + 1) % HistorySize;
            if (_historyCount < HistorySize) _historyCount++;

            Log.Debug($"[Outbox] Recorded {cmd.GetType().Name} for tick {cmd.TargetFrameIndex} " +
                      $"(history={_historyCount})");
        }

        /// <summary>
        ///     Retransmit recent commands from the history buffer.
        ///     Only the last RedundantSendCount entries are resent — not the
        ///     entire history. This provides ~400ms of redundancy at minimal
        ///     bandwidth cost (~5-10 extra commands per 200ms interval).
        ///
        ///     Called once per TICK_SYNC interval by the server (to clients)
        ///     and by the client (to server).
        /// </summary>
        public static void FlushRedundant()
        {
            if (_historyCount == 0)
                return;

            int toSend = _historyCount < RedundantSendCount
                ? _historyCount
                : RedundantSendCount;

            int sent = 0;

            // Start from the most recent entries and work backwards
            for (int i = 0; i < toSend; i++)
            {
                int idx = (_historyHead - 1 - i + HistorySize) % HistorySize;
                if (_history[idx].Data == null)
                    continue;

                // Send pre-serialized bytes directly — skip deserialize + re-serialize.
                // Tick-synced commands always use ReliableOrdered.
                if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server)
                {
                    MultiplayerManager.Instance.CurrentServer.SendRawToClients(
                        _history[idx].Data, DeliveryMethod.ReliableOrdered);
                }
                else if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Client)
                {
                    MultiplayerManager.Instance.CurrentClient.SendRawToServer(
                        _history[idx].Data, DeliveryMethod.ReliableOrdered);
                }

                sent++;
            }

            if (sent > 0)
            {
                Log.Debug($"[Outbox] Retransmitted {sent} recent commands");
            }
        }

        /// <summary>
        ///     Number of commands in the history buffer.
        /// </summary>
        public static int HistoryCount { get { return _historyCount; } }

        /// <summary>
        ///     Clear all state. Called on disconnect.
        /// </summary>
        public static void Clear()
        {
            for (int i = 0; i < HistorySize; i++)
            {
                _history[i] = default(HistoryEntry);
            }
            _historyHead = 0;
            _historyCount = 0;
        }
    }
}
