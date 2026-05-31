using CSM.API.Commands;
using ProtoBuf;

namespace CSM.Commands.Data.Sync
{
    /// <summary>
    ///     Bidirectional state hash exchange. Both server and clients
    ///     send their FNV-1a 64-bit hash of the current game state.
    ///     Receivers compare against their own hash to detect desync.
    ///
    ///     Per-system checksum traces:
    ///     SubsystemHashes contains individual hashes for economy, buildings,
    ///     networks, etc. When the aggregate hash mismatches, these per-system
    ///     hashes pinpoint WHICH subsystem diverged, enabling faster diagnosis.
    ///
    ///     Index mapping:
    ///       0 = Economy (cash)
    ///       1 = BuildIndex (simulation frame counter)
    ///       2 = Population
    ///       3 = Buildings (sampled)
    ///       4 = Networks (sampled)
    ///       5 = Trees
    ///       6 = Props
    ///       7 = Districts (including policies)
    ///       8 = Transport
    ///       9 = Vehicles
    ///      10 = TickClock (diagnostic only)
    ///      11 = Zones (sampled)
    /// </summary>
    [ProtoContract]
    public class StateHashCommand : CommandBase
    {
        [ProtoMember(1)]
        public uint Tick { get; set; }

        [ProtoMember(2)]
        public ulong Hash { get; set; }

        /// <summary>
        ///     Per-subsystem checksum traces. Each entry is an FNV-1a 64-bit hash
        ///     of one subsystem's state. Enables pinpointing WHICH subsystem
        ///     diverged on desync, instead of just knowing "something's wrong."
        /// </summary>
        [ProtoMember(4)]
        public SubsystemHashEntry[] SubsystemHashes { get; set; }
    }

    /// <summary>
    ///     A single subsystem's checksum trace.
    /// </summary>
    [ProtoContract]
    public class SubsystemHashEntry
    {
        /// <summary>
        ///     Subsystem identifier:
        ///     0=Economy, 1=BuildIndex, 2=Population, 3=Buildings,
        ///     4=Networks, 5=Trees, 6=Props, 7=Districts,
        ///     8=Transport, 9=Vehicles, 10=TickClock, 11=Zones.
        /// </summary>
        [ProtoMember(1)]
        public int SubsystemId { get; set; }

        /// <summary>FNV-1a 64-bit hash of this subsystem's state.</summary>
        [ProtoMember(2)]
        public ulong Hash { get; set; }
    }
}
