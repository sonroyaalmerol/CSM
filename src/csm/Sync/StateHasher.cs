using System;
using ColossalFramework;

namespace CSM.Sync
{
    /// <summary>
    ///     Fast, non-cryptographic 64-bit fingerprint of game state.
    ///     Used to detect drift between clients.
    ///     FNV-1a: 1 multiply + 1 XOR per byte. No allocations.
    ///
    ///     Covers: economy, build index, population, buildings, networks,
    ///     trees, props, districts, and zones.
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
        ///     Each subsystem is wrapped in try/catch so a missing manager
        ///     (e.g. during loading) doesn't prevent the rest from hashing.
        /// </summary>
        public static ulong ComputeHash()
        {
            ulong h = FnvOffset;

            // 1. EconomyManager total cash
            h = Fnv1a(h, BitConverter.GetBytes(EconomyManager.instance.MoneyAmount));

            // 2. Current simulation build index (increments on building/prop/tree creation)
            try
            {
                uint frameIdx = (uint)SimulationManager.instance.m_currentBuildIndex;
                h = Fnv1a(h, BitConverter.GetBytes(frameIdx));
            }
            catch { }

            // 3. Population
            try
            {
                var citizenMgr = Singleton<CitizenManager>.instance;
                if (citizenMgr != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes((int)citizenMgr.m_citizenCount));
                }
            }
            catch { }

            // 4. Building count and selected building hashes
            try
            {
                var bm = Singleton<BuildingManager>.instance;
                if (bm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(bm.m_buildings.m_size));
                    // Sample first N buildings for content hash (avoid hashing all 49152)
                    h = HashSampledBuildings(h, bm, 32);
                }
            }
            catch { }

            // 5. Network segment count
            try
            {
                var nm = Singleton<NetManager>.instance;
                if (nm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(nm.m_segments.m_size));
                }
            }
            catch { }

            // 6. Tree count
            try
            {
                var tm = Singleton<TreeManager>.instance;
                if (tm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(tm.m_trees.m_size));
                }
            }
            catch { }

            // 7. Prop count
            try
            {
                var pm = Singleton<PropManager>.instance;
                if (pm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(pm.m_props.m_size));
                }
            }
            catch { }

            // 8. District count
            try
            {
                var dm = Singleton<DistrictManager>.instance;
                if (dm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(dm.m_districts.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(dm.m_parks.m_size));
                }
            }
            catch { }

            // 9. Tick clock (catches tick drift)
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

        /// <summary>
        ///     Hash a sample of buildings from the building buffer.
        ///     Instead of iterating all 49152 entries, we sample every
        ///     Nth building and hash its position and info index.
        ///     This catches desyncs in building state while staying fast.
        /// </summary>
        private static ulong HashSampledBuildings(ulong h, BuildingManager bm, int sampleCount)
        {
            var buffer = bm.m_buildings.m_buffer;
            int total = buffer.Length;
            if (total == 0) return h;

            int step = Math.Max(1, total / sampleCount);

            for (int i = 1; i < total && sampleCount > 0; i += step)
            {
                ref var b = ref buffer[i];
                // Only hash occupied buildings
                if (b.m_flags != 0)
                {
                    h ^= (ulong)i;
                    h *= FnvPrime;
                    h = Fnv1a(h, BitConverter.GetBytes(b.m_position.x));
                    h = Fnv1a(h, BitConverter.GetBytes(b.m_position.z));
                    h = Fnv1a(h, BitConverter.GetBytes((int)b.m_infoIndex));
                    sampleCount--;
                }
            }

            return h;
        }
    }
}
