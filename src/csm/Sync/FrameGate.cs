using System;
using CSM.API;
using CSM.Commands;

namespace CSM.Sync
{
    /// <summary>
    ///     Frame gate: prevents the simulation from advancing past the
    ///     tick-stamped command frontier. Before each simulation tick,
    ///     this gate checks whether:
    ///       1. We haven't exceeded the server's max allowed tick
    ///       2. The command buffer for the next tick is ready
    ///     If conditions aren't met, the simulation pauses naturally.
    /// </summary>
    public static class FrameGate
    {
        /// <summary>
        ///     Called before each simulation tick.
        ///     Returns true if the simulation is allowed to advance one tick.
        ///     Returns false if the simulation should stall (frame gate closed).
        /// </summary>
        public static bool CanAdvance()
        {
            if (!TickClock.IsInitialized)
                return true; // Not connected, run freely

            uint nextTick = TickClock.LocalTick + 1;

            // Check 1: Don't run ahead of the server's pipeline window
            if (nextTick > TickClock.MaxAllowedTick)
            {
                Log.Debug($"[FrameGate] Stalled at tick {TickClock.LocalTick}: " +
                          $"next={nextTick} > maxAllowed={TickClock.MaxAllowedTick}");
                return false;
            }

            // Check 2: If there are commands buffered for the next tick,
            // wait until the tick is marked complete (all commands received).
            // If no commands are buffered for that tick, we can proceed freely.
            if (CommandBuffer.GetTickCommandCount(nextTick) > 0 && !CommandBuffer.IsReady(nextTick))
            {
                Log.Debug($"[FrameGate] Waiting for tick {nextTick} commands to complete");
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Execute all buffered commands for the given tick,
        ///     then advance the tick counter.
        ///     Called from OnAfterSimulationTick after the simulation step runs.
        /// </summary>
        public static void ExecuteTick(uint tick)
        {
            if (!TickClock.IsInitialized)
                return;

            var commands = CommandBuffer.DrainTick(tick);
            if (commands == null || commands.Count == 0)
            {
                TickClock.Advance();
                return;
            }

            foreach (var entry in commands)
            {
                try
                {
                    entry.Handler.Parse(entry.Command);
                }
                catch (Exception ex)
                {
                    Log.Error($"[FrameGate] Error executing {entry.Command.GetType().Name} at tick {tick}", ex);
                }
            }

            TickClock.Advance();
        }
    }
}
