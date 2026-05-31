using System;
using System.Text;
using CSM.API;
using CSM.API.Commands;
using CSM.Commands.Data.Sync;
using CSM.Sync;

namespace CSM.Commands.Handler.Sync
{
    public class StateHashHandler : CommandHandler<StateHashCommand>
    {
        public StateHashHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
            RequiresTickSync = false;
        }

        /// <summary>
        ///     Track the last received hashes per sender for deduplication.
        ///     Prevents redundant mismatch processing when the same sender's
        ///     redundant retransmission arrives twice.
        /// </summary>
        private static int _lastProcessedSenderId = -1;
        private static uint _lastProcessedTick;

        protected override void Handle(StateHashCommand command)
        {
            // Deduplicate: if we've already processed this sender's hash for this tick, skip.
            // This handles redundant retransmission from the outbox system.
            if (command.SenderId == _lastProcessedSenderId && command.Tick == _lastProcessedTick)
            {
                return;
            }

            ulong ourHash = StateHasher.ComputeHash();

            if (ourHash != command.Hash)
            {
                // Build per-system divergence report for diagnosis
                string divergence = BuildDivergenceReport(command);

                Log.Warn($"[Sync] STATE_HASH MISMATCH at tick {command.Tick}: " +
                         $"ours=0x{ourHash:X16}, theirs=0x{command.Hash:X16}, " +
                         $"sender={command.SenderId}" +
                         (divergence.Length > 0 ? $"\n{divergence}" : ""));

                DesyncDetector.OnHashMismatch(command.Tick, ourHash, command.Hash, command.SenderId, divergence);
            }
            else
            {
                Log.Debug($"[Sync] STATE_HASH verified at tick {command.Tick}");
                DesyncDetector.OnHashMatch(command.Tick);
            }

            _lastProcessedSenderId = command.SenderId;
            _lastProcessedTick = command.Tick;
        }

        /// <summary>
        ///     Compare our per-subsystem hashes against the received ones
        ///     and build a human-readable report showing which subsystems diverged.
        ///     This is the "checksum trace" technique from Wizard with a Gun —
        ///     per-system granularity narrows desync root cause from "something's
        ///     wrong" to "economy diverged at tick 12345."
        /// </summary>
        private static string BuildDivergenceReport(StateHashCommand command)
        {
            var ourHashes = StateHasher.GetSubsystemHashes();
            var theirHashes = command.SubsystemHashes;

            if (ourHashes == null || theirHashes == null || theirHashes.Length == 0)
                return "";

            var sb = new StringBuilder();
            sb.Append("[Sync] Divergent subsystems: ");

            bool anyDiverged = false;

            // Build lookup from their hashes
            for (int i = 0; i < ourHashes.Length; i++)
            {
                ulong ourSubHash = ourHashes[i].Hash;

                // Find matching subsystem in their hashes
                ulong theirSubHash = 0;
                bool found = false;
                for (int j = 0; j < theirHashes.Length; j++)
                {
                    if (theirHashes[j].SubsystemId == i)
                    {
                        theirSubHash = theirHashes[j].Hash;
                        found = true;
                        break;
                    }
                }

                if (found && ourSubHash != theirSubHash)
                {
                    if (anyDiverged) sb.Append(", ");
                    string name = i < StateHasher.SubsystemNames.Length
                        ? StateHasher.SubsystemNames[i]
                        : $"Subsystem{i}";
                    sb.Append($"{name}(0x{ourSubHash:X8} vs 0x{theirSubHash:X8})");
                    anyDiverged = true;
                }
            }

            if (!anyDiverged)
            {
                sb.Append("(per-system hashes match — divergence is in hash ordering or aggregate)");
            }

            return sb.ToString();
        }

        /// <summary>Reset deduplication state. Called on disconnect.</summary>
        public static void Reset()
        {
            _lastProcessedSenderId = -1;
            _lastProcessedTick = 0;
        }
    }
}
