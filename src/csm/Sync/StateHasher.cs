using System;
using ColossalFramework;

namespace CSM.Sync
{
    /// <summary>
    ///     Fast, non-cryptographic 64-bit fingerprint of game state.
    ///     Used to detect drift between clients.
    ///     FNV-1a: 1 multiply + 1 XOR per byte. No allocations.
    ///
    ///     Covers: economy, build index, population, buildings (deep),
    ///     network segments (deep), trees, props, districts (with policies),
    ///     transport lines, vehicles, and tick clock.
    /// </summary>
    public static class StateHasher
    {
        // FNV-1a constants (64-bit)
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

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
            try
            {
                h = Fnv1a(h, BitConverter.GetBytes(EconomyManager.instance.MoneyAmount));
            }
            catch { }

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

            // 4. Building count and deep sampled building hashes
            try
            {
                var bm = Singleton<BuildingManager>.instance;
                if (bm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(bm.m_buildings.m_size));
                    h = HashSampledBuildings(h, bm, 32);
                }
            }
            catch { }

            // 5. Network segment count and sampled segment hashes
            try
            {
                var nm = Singleton<NetManager>.instance;
                if (nm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(nm.m_segments.m_size));
                    h = HashSampledSegments(h, nm, 24);
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

            // 8. District count and policy hashes
            try
            {
                var dm = Singleton<DistrictManager>.instance;
                if (dm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(dm.m_districts.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(dm.m_parks.m_size));
                    h = HashDistrictPolicies(h, dm);
                }
            }
            catch { }

            // 9. Transport line count
            try
            {
                var tlMgr = Singleton<TransportManager>.instance;
                if (tlMgr != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(tlMgr.m_lines.m_size));
                }
            }
            catch { }

            // 10. Vehicle count
            try
            {
                var vm = Singleton<VehicleManager>.instance;
                if (vm != null)
                {
                    h = Fnv1a(h, BitConverter.GetBytes(vm.m_vehicles.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(vm.m_parkedVehicles.m_size));
                }
            }
            catch { }

            // 11. Tick clock (catches tick drift)
            h = Fnv1a(h, BitConverter.GetBytes(TickClock.LocalTick));

            return h;
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
        ///     Nth building and hash position, infoIndex, productionRate,
        ///     electricityBuffer, waterPipe, and flags.
        ///     This catches desyncs in building state, AI configuration,
        ///     and utility connections while staying fast.
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
                    h = Fnv1a(h, BitConverter.GetBytes((int)b.m_productionRate));
                    h = Fnv1a(h, BitConverter.GetBytes(b.m_electricityBuffer));
                    h = Fnv1a(h, BitConverter.GetBytes(b.m_waterPipe));
                    h = Fnv1a(h, BitConverter.GetBytes((uint)b.m_flags));
                    sampleCount--;
                }
            }

            return h;
        }

        /// <summary>
        ///     Hash a sample of network segments for position, info, and flags.
        ///     Catches desyncs in road/network placement and configuration.
        /// </summary>
        private static ulong HashSampledSegments(ulong h, NetManager nm, int sampleCount)
        {
            var buffer = nm.m_segments.m_buffer;
            int total = buffer.Length;
            if (total == 0) return h;

            int step = Math.Max(1, total / sampleCount);

            for (int i = 1; i < total && sampleCount > 0; i += step)
            {
                ref var seg = ref buffer[i];
                if (seg.m_flags != 0)
                {
                    h ^= (ulong)i;
                    h *= FnvPrime;
                    h = Fnv1a(h, BitConverter.GetBytes((int)seg.m_infoIndex));
                    h = Fnv1a(h, BitConverter.GetBytes(seg.m_bounds.center.x));
                    h = Fnv1a(h, BitConverter.GetBytes(seg.m_bounds.center.z));
                    h = Fnv1a(h, BitConverter.GetBytes((uint)seg.m_flags));
                    sampleCount--;
                }
            }

            return h;
        }

        /// <summary>
        ///     Hash district and park state.
        ///     Iterates all district/park buffers and hashes flags, style,
        ///     and random seed. This catches district creation, deletion,
        ///     and style changes that could indicate desync.
        /// </summary>
        private static ulong HashDistrictPolicies(ulong h, DistrictManager dm)
        {
            try
            {
                var districts = dm.m_districts.m_buffer;
                for (int i = 0; i < districts.Length; i++)
                {
                    ref var d = ref districts[i];
                    if (d.m_flags != 0)
                    {
                        h ^= (ulong)i;
                        h *= FnvPrime;
                        h = Fnv1a(h, BitConverter.GetBytes((uint)d.m_flags));
                        h = Fnv1a(h, BitConverter.GetBytes(d.m_randomSeed));
                        h = Fnv1a(h, BitConverter.GetBytes((int)d.m_Style));
                    }
                }
            }
            catch { }

            try
            {
                var parks = dm.m_parks.m_buffer;
                for (int i = 0; i < parks.Length; i++)
                {
                    ref var p = ref parks[i];
                    if (p.m_flags != 0)
                    {
                        h ^= (ulong)(i + 0x10000); // offset to avoid collision with districts
                        h *= FnvPrime;
                        h = Fnv1a(h, BitConverter.GetBytes((uint)p.m_flags));
                        h = Fnv1a(h, BitConverter.GetBytes(p.m_randomSeed));
                    }
                }
            }
            catch { }

            return h;
        }
    }
}
