using System;
using ColossalFramework;

namespace CSM.Sync
{
    /// <summary>
    ///     Fast, non-cryptographic 64-bit fingerprint of game state.
    ///     Used to detect drift between clients.
    ///     FNV-1a: 1 multiply + 1 XOR per byte. No allocations.
    /// </summary>
    public static class StateHasher
    {
        // FNV-1a constants (64-bit)
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        ///     Hash interval: only compute and compare every N ticks.
        ///     At 60fps, every 60 ticks = once per second.
        /// </summary>
        public const uint HashInterval = 60;

        /// <summary>
        ///     Compute a 64-bit hash of critical game state.
        ///     Should be called at the same tick on all clients.
        /// </summary>
        public static ulong ComputeHash()
        {
            ulong h = FnvOffset;

            // 1. EconomyManager total cash
            h = Fnv1a(h, BitConverter.GetBytes(EconomyManager.instance.MoneyAmount));

            // 2. Current simulation frame index
            try
            {
                uint frameIdx = (uint)SimulationManager.instance.m_currentBuildIndex;
                h = Fnv1a(h, BitConverter.GetBytes(frameIdx));
            }
            catch { }

            // 3. Population
            try
            {
                int pop = 0;
                var citizenMgr = Singleton<CitizenManager>.instance;
                if (citizenMgr != null)
                {
                    pop = (int)citizenMgr.m_citizenCount;
                }
                h = Fnv1a(h, BitConverter.GetBytes(pop));
            }
            catch { }

            // 4. Tick clock
            h = Fnv1a(h, BitConverter.GetBytes(TickClock.LocalTick));

            return h;
        }

        /// <summary>Check whether a hash should be computed at this tick.</summary>
        public static bool ShouldHash(uint tick)
        {
            return tick > 0 && (tick % HashInterval) == 0;
        }

        // ── FNV-1a core ────────────────────────────────────
        // One multiply + one XOR per byte. Faster than MD5/SHA by ~50x.
        // Avalanche properties sufficient for drift detection.

        private static ulong Fnv1a(ulong h, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= FnvPrime;
            }
            return h;
        }
    }
}
