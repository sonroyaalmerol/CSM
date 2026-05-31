using System;
using CSM.API;

namespace CSM.Sync
{
    /// <summary>
    ///     Frame gate: prevents the simulation from advancing past the
    ///     server's pipeline window. Before each simulation tick,
    ///     this gate checks whether the next tick exceeds MaxAllowedTick.
    ///     If so, the simulation pauses until a TICK_SYNC arrives with
    ///     an updated window.
    ///
    ///     Coupling contract with SpeedPauseHelper and DesyncDetector:
    ///     ──────────────────────────────────────────────────────────
    ///     Three systems can set m_simulationPaused:
    ///       1. FrameGate (tick-sync stall)          → IsGateClosed
    ///       2. DesyncDetector (hash mismatch)       → IsDesyncPaused
    ///       3. SpeedPauseHelper (speed/pause UI)    → no flag
    ///
    ///     SpeedPauseHelper.SimulationStep() checks both IsGateClosed and
    ///     IsDesyncPaused before negotiating pause/speed changes. This prevents
    ///     the systems from fighting over the pause state.
    ///
    ///     Execution order matters:
    ///       1. SpeedPauseHelper.SimulationStep() runs in OnBeforeSimulationTick (top)
    ///       2. FrameGate.CanAdvance() runs in OnBeforeSimulationTick (after SpeedPause)
    ///       3. DesyncDetector.OnHashMismatch() runs in StateHashHandler (network receive)
    ///     Since all run on the main thread (LiteNetLib polls inline),
    ///     there is no concurrent access to m_simulationPaused.
    /// </summary>
    public static class FrameGate
    {
        /// <summary>
        ///     True when the gate is currently closed (simulation stalled).
        ///     Checked by SpeedPauseHelper to avoid interfering with pause
        ///     negotiations while the tick gate is active.
        /// </summary>
        public static bool IsGateClosed { get; private set; }

        /// <summary>
        ///     True on the frame the gate transitions from closed to open.
        ///     Used by ThreadingExtension to unpause the simulation exactly once.
        /// </summary>
        public static bool JustOpened { get; private set; }

        /// <summary>
        ///     Called before each simulation tick.
        ///     Returns true if the simulation is allowed to advance one tick.
        ///     Returns false if the simulation should stall (frame gate closed).
        ///
        ///     The gate only checks that we haven't exceeded the server's
        ///     pipeline window. Commands are drained by ExecuteTick() when
        ///     the tick actually runs. There is no "tick complete" signal —
        ///     the server's target tick assignment is the sole coordination
        ///     mechanism.
        /// </summary>
        public static bool CanAdvance()
        {
            JustOpened = false;

            if (!TickClock.IsInitialized)
            {
                IsGateClosed = false;
                return true; // Not connected, run freely
            }

            uint nextTick = TickClock.LocalTick + 1;

            // Don't run ahead of the server's pipeline window
            if (nextTick > TickClock.MaxAllowedTick)
            {
                Log.Debug($"[FrameGate] Stalled at tick {TickClock.LocalTick}: " +
                          $"next={nextTick} > maxAllowed={TickClock.MaxAllowedTick}");
                IsGateClosed = true;
                return false;
            }

            // Gate transition: closed → open
            if (IsGateClosed)
                JustOpened = true;

            IsGateClosed = false;
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
