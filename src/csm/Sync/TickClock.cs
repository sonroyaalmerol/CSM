using System;
using CSM.API;
using CSM.API.Commands;
using CSM.Networking;

namespace CSM.Sync
{
    /// <summary>
    ///     Server-authoritative tick clock. All clients agree on the
    ///     pipeline window: [serverTick .. serverTick + pipelineDepth].
    ///     Clients must not advance past serverTick + pipelineDepth.
    ///     Commands are buffered and executed at their target tick.
    ///
    ///     Tick continuity: ticks reset to 0 on server restart.
    ///     All clients must reconnect after a server crash/restart.
    ///     Tick values are unsigned and monotonically increasing within
    ///     a session — they wrap at uint.MaxValue (~2 years at 60fps).
    /// </summary>
    public static class TickClock
    {
        // ── Tick state ─────────────────────────────────────
        private static uint _localTick;
        private static uint _serverTick;
        private static uint _pipelineDepth;
        private static bool _initialized;

        // ── Pipeline tuning ────────────────────────────────
        // Minimum pipeline depth in ticks. At 60fps, 1 tick ≈ 16.6ms.
        // We want at least 2 round-trips of buffer for safety.
        private const uint MinPipelineDepth = 12;  // ~200ms
        private const uint MaxPipelineDepth = 180; // ~3000ms (supports up to ~750ms one-way)
        private const uint DefaultPipelineDepth = 20; // ~333ms

        /// <summary>This client's current simulation tick.</summary>
        public static uint LocalTick { get { return _localTick; } }

        /// <summary>Last known server tick (from TICK_SYNC).</summary>
        public static uint ServerTick { get { return _serverTick; } }

        /// <summary>How many ticks ahead of server we're allowed to run.</summary>
        /// <remarks>
        ///     Write contract:
        ///     - Server: set by CalculatePipelineDepth() based on max client latency.
        ///       This value is sent to clients via TICK_SYNC.
        ///     - Client: set by OnServerTick() from the server's TICK_SYNC packet.
        ///       Clients never call CalculatePipelineDepth().
        ///     The server is the sole authority for pipeline depth.
        /// </remarks>
        public static uint PipelineDepth
        {
            get { return _pipelineDepth; }
            set { _pipelineDepth = Math.Max(MinPipelineDepth, Math.Min(MaxPipelineDepth, value)); }
        }

        /// <summary>Maximum tick this client is allowed to advance to.</summary>
        public static uint MaxAllowedTick
        {
            get { return _serverTick + _pipelineDepth; }
        }

        /// <summary>Called when the simulation completes a tick.</summary>
        public static void Advance()
        {
            _localTick++;
        }

        /// <summary>
        ///     Called when a TICK_SYNC arrives from the server.
        ///     Updates our knowledge of the server's tick position.
        /// </summary>
        public static void OnServerTick(uint serverTick, uint pipelineDepth)
        {
            _serverTick = serverTick;
            _pipelineDepth = pipelineDepth;

            // If we somehow got ahead of the allowed window, snap back.
            // This shouldn't happen with a working frame gate, but is a safety net.
            if (_localTick > MaxAllowedTick)
            {
                Log.Warn($"[TickClock] Client was ahead of allowed window. " +
                         $"Local={_localTick}, MaxAllowed={MaxAllowedTick}. Snapping back.");
                _localTick = MaxAllowedTick;
            }
        }

        /// <summary>
        ///     Calculate optimal pipeline depth based on network latency.
        ///     Higher latency → deeper pipeline to avoid stalls.
        ///     Only called by the server — stores the result so relay
        ///     stamping uses the same value sent in TICK_SYNC.
        ///     Clients receive the server's depth via OnServerTick().
        /// </summary>
        public static uint CalculatePipelineDepth()
        {
            long maxLatency = GetMaxLatencyMs();

            if (maxLatency <= 0) return _pipelineDepth;

            // Convert ms to ticks (60 ticks/sec → 16.6ms/tick)
            uint ticksForLatency = (uint)((maxLatency * 60) / 1000);

            // Pipeline = 2x latency ticks + safety margin
            uint depth = ticksForLatency * 2 + 6;

            depth = Math.Max(MinPipelineDepth, Math.Min(MaxPipelineDepth, depth));

            // Store so relay stamping uses the same value sent in TICK_SYNC
            _pipelineDepth = depth;
            return depth;
        }

        /// <summary>Initialize the clock. Called on game load / connect.</summary>
        public static void Initialize(uint startTick)
        {
            _localTick = startTick;
            _serverTick = startTick;
            _pipelineDepth = DefaultPipelineDepth;
            _initialized = true;
            Log.Info($"[TickClock] Initialized at tick {startTick}, pipeline depth {DefaultPipelineDepth}");
        }

        /// <summary>Reset the clock. Called on disconnect.</summary>
        public static void Reset()
        {
            _localTick = 0;
            _serverTick = 0;
            _pipelineDepth = DefaultPipelineDepth;
            _initialized = false;
        }

        public static bool IsInitialized { get { return _initialized; } }

        // ── Internals ──────────────────────────────────────

        internal static long GetMaxLatencyMs()
        {
            if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Client)
            {
                return MultiplayerManager.Instance.CurrentClient.ClientPlayer.Latency;
            }
            else if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server)
            {
                long max = 0;
                foreach (var player in MultiplayerManager.Instance.CurrentServer.ConnectedPlayers.Values)
                {
                    if (player.Latency > max) max = player.Latency;
                }
                return max;
            }
            return 0;
        }
    }
}
