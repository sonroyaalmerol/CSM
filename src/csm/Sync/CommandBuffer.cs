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
            /// <summary>True when the server confirmed no more commands for this tick.</summary>
            public bool Complete;

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
        /// </summary>
        /// <returns>true if buffered, false if tick already passed.</returns>
        public static bool Buffer(uint targetTick, CommandHandler handler, CommandBase cmd, uint currentTick)
        {
            // If the target tick is in the past, don't buffer — let caller execute immediately.
            if (targetTick <= currentTick)
            {
                return false;
            }

            int idx = (int)(targetTick & RING_MASK);

            // Guard against ring collision (extremely unlikely with 256-slot ring).
            // If the slot is still marked complete from a previous rotation, clear it.
            ref TickSlot slot = ref _ring[idx];
            if (slot.Commands != null && slot.Complete)
            {
                slot.Commands.Clear();
                slot.Complete = false;
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
        ///     Mark a tick as complete (server confirmed no more commands).
        /// </summary>
        public static void MarkComplete(uint tick)
        {
            int idx = (int)(tick & RING_MASK);
            _ring[idx].Complete = true;
            Log.Debug($"[CommandBuffer] Tick {tick} marked complete (idx={idx})");
        }

        /// <summary>
        ///     Check if a tick is ready to execute (has all expected commands
        ///     or is marked complete by the server).
        /// </summary>
        public static bool IsReady(uint tick)
        {
            int idx = (int)(tick & RING_MASK);
            return _ring[idx].Complete;
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
            slot.Complete = false;

            if (count > 0)
            {
                Log.Debug($"[CommandBuffer] Drained {count} commands for tick {tick}");
            }

            return result;
        }

        /// <summary>
        ///     Get the count of buffered commands for a specific tick.
        /// </summary>
        public static int GetTickCommandCount(uint tick)
        {
            int idx = (int)(tick & RING_MASK);
            List<BufferedCommand> list = _ring[idx].Commands;
            return list != null ? list.Count : 0;
        }

        /// <summary>Clear the entire buffer. Called on disconnect.</summary>
        public static void Clear()
        {
            for (int i = 0; i < RING_SIZE; i++)
            {
                _ring[i].Commands = null;
                _ring[i].Complete = false;
            }
            TotalBuffered = 0;
        }
    }
}
