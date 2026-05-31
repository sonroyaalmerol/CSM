using System.Collections.Generic;
using CSM.API;
using CSM.API.Commands;

namespace CSM.Sync
{
    /// <summary>
    ///     Tick-indexed ring buffer for commands awaiting execution.
    ///     Uses modular arithmetic for O(1) tick-slot lookup.
    ///     256 slots = ~4.2 seconds at 60fps. Far beyond any network jitter window.
    /// </summary>
    public static class CommandBuffer
    {
        // Power-of-2 ring size enables bitmask instead of modulo.
        // 256 ticks @ 60fps ≈ 4.26 seconds of lookahead.
        private const int RING_SIZE = 256;
        private const int RING_MASK = RING_SIZE - 1;

        private struct TickSlot
        {
            /// <summary>Buffered commands for this tick.</summary>
            public List<BufferedCommand> Commands;
        }

        /// <summary>
        ///     A command awaiting execution at its target tick.
        ///     Handler and Command are pre-deserialized from protobuf
        ///     to avoid deserialization cost at execution time.
        /// </summary>
        public struct BufferedCommand
        {
            public CommandHandler Handler;
            public CommandBase Command;
        }

        private static readonly TickSlot[] _ring = new TickSlot[RING_SIZE];

        /// <summary>Number of commands buffered across all ticks.</summary>
        public static int TotalBuffered { get; private set; }

        /// <summary>
        ///     Buffer a command for execution at the given target tick.
        ///     If the target tick has already passed, the command is executed immediately
        ///     by the caller (this method returns false).
        ///     Deduplicates redundant retransmissions by checking if the command
        ///     type + target tick already exists in the slot.
        /// </summary>
        /// <returns>true if buffered, false if tick already passed or duplicate.</returns>
        public static bool Buffer(uint targetTick, CommandHandler handler, CommandBase cmd, uint currentTick)
        {
            // If the target tick is in the past, don't buffer — let caller execute immediately.
            if (targetTick <= currentTick)
            {
                return false;
            }

            int idx = (int)(targetTick & RING_MASK);

            // Guard against ring collision
            ref TickSlot slot = ref _ring[idx];
            if (slot.Commands != null && slot.Commands.Count > 0)
            {
                int distance = (int)(targetTick - currentTick);
                if (distance >= RING_SIZE)
                {
                    Log.Warn($"[CommandBuffer] Target tick {targetTick} is {distance} ticks ahead — " +
                             $"exceeds ring size {RING_SIZE}. Dropping stale slot and buffering.");
                    TotalBuffered -= slot.Commands.Count;
                    slot.Commands.Clear();
                }
                else
                {
                    // Deduplication: check if this exact command (same SendSeq)
                    // is already buffered for this tick. Handles redundant
                    // retransmissions from the Outbox without dropping different
                    // commands of the same type from the same sender.
                    if (cmd.SendSeq != 0)
                    {
                        for (int i = 0; i < slot.Commands.Count; i++)
                        {
                            if (slot.Commands[i].Command.SendSeq == cmd.SendSeq)
                            {
                                Log.Debug($"[CommandBuffer] Duplicate {cmd.GetType().Name} " +
                                          $"(seq={cmd.SendSeq}) from sender {cmd.SenderId} " +
                                          $"for tick {targetTick} — dropping redundant retransmission.");
                                return false;
                            }
                        }
                    }
                }
            }

            if (slot.Commands == null)
            {
                slot.Commands = new List<BufferedCommand>(4);
            }

            slot.Commands.Add(new BufferedCommand
            {
                Handler = handler,
                Command = cmd
            });

            TotalBuffered++;

            Log.Debug($"[CommandBuffer] Buffered {cmd.GetType().Name} for tick {targetTick} " +
                      $"(current={currentTick}, idx={idx}, total={TotalBuffered})");

            return true;
        }

        /// <summary>
        ///     Drain all commands for the given tick and return them.
        ///     Clears the slot after draining.
        /// </summary>
        public static List<BufferedCommand> DrainTick(uint tick)
        {
            int idx = (int)(tick & RING_MASK);
            ref TickSlot slot = ref _ring[idx];

            List<BufferedCommand> result = slot.Commands;
            int count = result != null ? result.Count : 0;
            TotalBuffered -= count;

            // Clear slot for reuse
            slot.Commands = null;

            if (count > 0)
            {
                Log.Debug($"[CommandBuffer] Drained {count} commands for tick {tick}");
            }

            return result;
        }

        /// <summary>Clear the entire buffer. Called on disconnect.</summary>
        public static void Clear()
        {
            for (int i = 0; i < RING_SIZE; i++)
            {
                _ring[i].Commands = null;
            }
            TotalBuffered = 0;
        }
    }
}
