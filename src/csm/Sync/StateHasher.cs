using System;
using ColossalFramework;
using CSM.API.Helpers;
using CSM.Commands.Data.Sync;

namespace CSM.Sync
{
    /// <summary>
    ///     Fast, non-cryptographic 64-bit fingerprint of game state.
    ///     Used to detect drift between clients.
    ///     FNV-1a: 1 multiply + 1 XOR per byte. No allocations.
    ///
    ///     Computes both an aggregate hash and per-subsystem checksums
    ///     so that desync diagnosis can pinpoint WHICH subsystem diverged
    ///     instead of just knowing "something's wrong."
    ///
    ///     Subsystem IDs:
    ///       0 = Economy, 1 = BuildIndex, 2 = Population, 3 = Buildings,
    ///       4 = Networks, 5 = Trees, 6 = Props, 7 = Districts,
    ///       8 = Transport, 9 = Vehicles, 10 = TickClock, 11 = Zones.
    /// </summary>
    public static class StateHasher
    {
        // FNV-1a constants (64-bit)
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        ///     Subsystem IDs matching the indices in StateHashCommand.
        /// </summary>
        public const int Sub_Economy = 0;
        public const int Sub_BuildIndex = 1;
        public const int Sub_Population = 2;
        public const int Sub_Buildings = 3;
        public const int Sub_Networks = 4;
        public const int Sub_Trees = 5;
        public const int Sub_Props = 6;
        public const int Sub_Districts = 7;
        public const int Sub_Transport = 8;
        public const int Sub_Vehicles = 9;
        public const int Sub_TickClock = 10;
        public const int Sub_Zones = 11;
        public const int SubsystemCount = 12;

        /// <summary>
        ///     Human-readable names for each subsystem ID.
        ///     Used in desync log messages for diagnosis.
        /// </summary>
        public static readonly string[] SubsystemNames = new string[]
        {
            "Economy", "BuildIndex", "Population", "Buildings",
            "Networks", "Trees", "Props", "Districts",
            "Transport", "Vehicles", "TickClock", "Zones"
        };

        /// <summary>
        ///     Compute a 64-bit hash of critical game state.
        ///     Should be called at the same tick on all clients.
        ///     Each subsystem is wrapped in try/catch so a missing manager
        ///     (e.g. during loading) doesn't prevent the rest from hashing.
        ///     The aggregate hash EXCLUDES TickClock.LocalTick because
        ///     server and client are always at different ticks.
        /// </summary>
        public static ulong ComputeHash()
        {
            ulong h = FnvOffset;
            var hashes = new SubsystemHashEntry[SubsystemCount];

            for (int i = 0; i < SubsystemCount; i++)
            {
                hashes[i] = new SubsystemHashEntry { SubsystemId = i, Hash = 0 };
            }

            // 1. EconomyManager total cash
            try
            {
                long cash = (long)ReflectionHelper.GetAttr<object>(EconomyManager.instance, "m_cashAmount");
                hashes[Sub_Economy].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(cash));
                h = Fnv1a(h, BitConverter.GetBytes(cash));
            }
            catch { }

            // 2. Current simulation build index
            try
            {
                uint frameIdx = (uint)SimulationManager.instance.m_currentBuildIndex;
                hashes[Sub_BuildIndex].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(frameIdx));
                h = Fnv1a(h, BitConverter.GetBytes(frameIdx));
            }
            catch { }

            // 3. Population
            try
            {
                var citizenMgr = Singleton<CitizenManager>.instance;
                if (citizenMgr != null)
                {
                    hashes[Sub_Population].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(citizenMgr.m_citizens.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(citizenMgr.m_citizens.m_size));
                }
            }
            catch { }

            // 4. Building count and deep sampled building hashes
            try
            {
                var bm = Singleton<BuildingManager>.instance;
                if (bm != null)
                {
                    ulong bHash = FnvOffset;
                    bHash = Fnv1a(bHash, BitConverter.GetBytes(bm.m_buildings.m_size));
                    bHash = HashSampledBuildings(bHash, bm, 32);
                    hashes[Sub_Buildings].Hash = bHash;
                    h = Fnv1a(h, BitConverter.GetBytes(bm.m_buildings.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(bHash));
                }
            }
            catch { }

            // 5. Network segment count and sampled segment hashes
            try
            {
                var nm = Singleton<NetManager>.instance;
                if (nm != null)
                {
                    ulong nHash = FnvOffset;
                    nHash = Fnv1a(nHash, BitConverter.GetBytes(nm.m_segments.m_size));
                    nHash = HashSampledSegments(nHash, nm, 24);
                    hashes[Sub_Networks].Hash = nHash;
                    h = Fnv1a(h, BitConverter.GetBytes(nm.m_segments.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(nHash));
                }
            }
            catch { }

            // 6. Tree count
            try
            {
                var tm = Singleton<TreeManager>.instance;
                if (tm != null)
                {
                    hashes[Sub_Trees].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(tm.m_trees.m_size));
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
                    hashes[Sub_Props].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(pm.m_props.m_size));
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
                    ulong dHash = FnvOffset;
                    dHash = Fnv1a(dHash, BitConverter.GetBytes(dm.m_districts.m_size));
                    dHash = Fnv1a(dHash, BitConverter.GetBytes(dm.m_parks.m_size));
                    dHash = HashDistrictPolicies(dHash, dm);
                    hashes[Sub_Districts].Hash = dHash;
                    h = Fnv1a(h, BitConverter.GetBytes(dm.m_districts.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(dm.m_parks.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(dHash));
                }
            }
            catch { }

            // 9. Transport line count
            try
            {
                var tlMgr = Singleton<TransportManager>.instance;
                if (tlMgr != null)
                {
                    hashes[Sub_Transport].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(tlMgr.m_lines.m_size));
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
                    ulong vHash = FnvOffset;
                    vHash = Fnv1a(vHash, BitConverter.GetBytes(vm.m_vehicles.m_size));
                    vHash = Fnv1a(vHash, BitConverter.GetBytes(vm.m_parkedVehicles.m_size));
                    hashes[Sub_Vehicles].Hash = vHash;

                    h = Fnv1a(h, BitConverter.GetBytes(vm.m_vehicles.m_size));
                    h = Fnv1a(h, BitConverter.GetBytes(vm.m_parkedVehicles.m_size));
                }
            }
            catch { }

            // 11. Zone blocks (sampled)
            try
            {
                var zm = Singleton<ZoneManager>.instance;
                if (zm != null)
                {
                    ulong zHash = FnvOffset;
                    zHash = Fnv1a(zHash, BitConverter.GetBytes(zm.m_blocks.m_size));
                    var blocks = zm.m_blocks.m_buffer;
                    int bTotal = blocks != null ? blocks.Length : 0;
                    int bStep = Math.Max(1, bTotal / 32);
                    for (int i = 1; i < bTotal; i += bStep)
                    {
                        ref var blk = ref blocks[i];
                        zHash ^= (ulong)i;
                        zHash *= FnvPrime;
                        zHash = Fnv1a(zHash, BitConverter.GetBytes(blk.m_zone1));
                        zHash = Fnv1a(zHash, BitConverter.GetBytes(blk.m_zone2));
                    }
                    hashes[Sub_Zones].Hash = zHash;
                    // Mix zone subsystem hash into aggregate
                    h = Fnv1a(h, BitConverter.GetBytes(zHash));
                }
            }
            catch { }

            // 12. Tick clock (stored as subsystem hash for diagnostics,
            // but NOT mixed into the aggregate hash — server and client
            // are always at different local ticks).
            hashes[Sub_TickClock].Hash = Fnv1a(FnvOffset, BitConverter.GetBytes(TickClock.LocalTick));

            // Store per-system hashes for retrieval
            _lastSubsystemHashes = hashes;

            return h;
        }

        /// <summary>
        ///     Get the per-subsystem hashes from the last ComputeHash() call.
        ///     Returns null if ComputeHash() hasn't been called yet.
        /// </summary>
        public static SubsystemHashEntry[] GetSubsystemHashes()
        {
            return _lastSubsystemHashes;
        }

        private static SubsystemHashEntry[] _lastSubsystemHashes;

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
        ///     and flags.
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
                    h = Fnv1a(h, BitConverter.GetBytes((uint)b.m_flags));
                    sampleCount--;
                }
            }

            return h;
        }

        /// <summary>
        ///     Hash a sample of network segments for position, info, and flags.
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
