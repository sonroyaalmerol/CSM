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
    ///
    ///     Adaptive pipeline depth:
    ///     ────────────────────────
    ///     Uses EWMA (exponentially weighted moving average) to smooth
    ///     latency readings and prevent the pipeline from jumping wildly
    ///     on individual spikes. Pipeline depth transitions are rate-limited
    ///     so the game never experiences sudden speed changes.
    ///     Inspired by Age of Empires' turn-length controller: "Consistent
    ///     500ms lag feels better than variable 80-500ms lag."
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

        // ── Adaptive pipeline (EWMA) ───────────────────────
        // EWMA alpha: controls how quickly the smoothed latency
        // responds to new readings. 0.2 = fairly smooth, takes ~5
        // readings to converge. Higher = more responsive but noisier.
        private const double EwmaAlpha = 0.2;

        // Maximum pipeline depth change per TICK_SYNC interval (200ms).
        // At 60fps, 2 ticks per interval means depth changes gradually.
        // This prevents the "jerky" feeling from sudden pipeline jumps.
        private const uint MaxPipelineChangePerInterval = 3;

        /// <summary>
        ///     Smoothed (EWMA) max latency in milliseconds.
        ///     Updated on every latency sample from LiteNetLib.
        ///     Prevents pipeline depth from reacting to single spikes.
        /// </summary>
        private static double _smoothedMaxLatencyMs;

        /// <summary>
        ///     Whether the EWMA has received its first sample.
        ///     Until then, CalculatePipelineDepth uses the raw value.
        /// </summary>
        private static bool _ewmaInitialized;

        /// <summary>
        ///     The last pipeline depth we computed. Used to rate-limit
        ///     transitions so depth changes smoothly.
        /// </summary>
        private static uint _lastComputedDepth;

        /// <summary>This client's current simulation tick.</summary>
        public static uint LocalTick { get { return _localTick; } }

        /// <summary>Last known server tick (from TICK_SYNC).</summary>
        public static uint ServerTick { get { return _serverTick; } }

        /// <summary>
        ///     How many ticks ahead of server we're allowed to run.
        ///     Set by the server based on EWMA-smoothed max latency.
        /// </summary>
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
        ///     Feed a new latency sample into the EWMA smoother.
        ///     Called from the LiteNetLib latency update callbacks
        ///     (Server.ListenerOnNetworkLatencyUpdateEvent,
        ///      Client.ListenerOnNetworkLatencyUpdateEvent).
        ///     Samples arrive every ~2 seconds (PingInterval).
        /// </summary>
        public static void UpdateLatencySample(long rawLatencyMs)
        {
            if (rawLatencyMs <= 0)
                return;

            if (!_ewmaInitialized)
            {
                _smoothedMaxLatencyMs = rawLatencyMs;
                _ewmaInitialized = true;
                return;
            }

            // EWMA: new = alpha * sample + (1 - alpha) * old
            _smoothedMaxLatencyMs = EwmaAlpha * rawLatencyMs +
                                    (1.0 - EwmaAlpha) * _smoothedMaxLatencyMs;
        }

        /// <summary>
        ///     Calculate optimal pipeline depth based on EWMA-smoothed latency.
        ///     Only called by the server on each TICK_SYNC interval.
        ///     Depth changes are rate-limited to prevent sudden jumps.
        /// </summary>
        public static uint CalculatePipelineDepth()
        {
            // Use the EWMA-smoothed latency (already updated by LiteNetLib callbacks).
            // Do NOT call UpdateLatencySample here — that would create duplicate
            // samples between LiteNetLib events and bias the EWMA.
            double latencyMs = _ewmaInitialized ? _smoothedMaxLatencyMs : GetMaxLatencyMs();

            if (latencyMs <= 0) return _pipelineDepth;

            // Convert ms to ticks (60 ticks/sec → 16.6ms/tick)
            uint ticksForLatency = (uint)((latencyMs * 60) / 1000);

            // Pipeline = 2x latency ticks + safety margin
            uint targetDepth = ticksForLatency * 2 + 6;

            targetDepth = Math.Max(MinPipelineDepth, Math.Min(MaxPipelineDepth, targetDepth));

            // ── Rate-limited transition ─────────────────────
            // Never change the pipeline by more than MaxPipelineChangePerInterval
            // per TICK_SYNC (200ms). This makes speed transitions smooth.
            uint prevDepth = _lastComputedDepth;
            uint delta;

            if (targetDepth > prevDepth)
            {
                delta = Math.Min(targetDepth - prevDepth, MaxPipelineChangePerInterval);
                targetDepth = prevDepth + delta;
            }
            else if (targetDepth < prevDepth)
            {
                delta = Math.Min(prevDepth - targetDepth, MaxPipelineChangePerInterval);
                targetDepth = prevDepth - delta;
            }

            _lastComputedDepth = targetDepth;
            _pipelineDepth = targetDepth;
            return targetDepth;
        }

        /// <summary>Initialize the clock. Called on game load / connect.</summary>
        public static void Initialize(uint startTick)
        {
            _localTick = startTick;
            _serverTick = startTick;
            _pipelineDepth = DefaultPipelineDepth;
            _lastComputedDepth = DefaultPipelineDepth;
            _smoothedMaxLatencyMs = 0;
            _ewmaInitialized = false;
            _initialized = true;
            Log.Info($"[TickClock] Initialized at tick {startTick}, pipeline depth {DefaultPipelineDepth}");
        }

        /// <summary>Reset the clock. Called on disconnect.</summary>
        public static void Reset()
        {
            _localTick = 0;
            _serverTick = 0;
            _pipelineDepth = DefaultPipelineDepth;
            _lastComputedDepth = DefaultPipelineDepth;
            _smoothedMaxLatencyMs = 0;
            _ewmaInitialized = false;
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
