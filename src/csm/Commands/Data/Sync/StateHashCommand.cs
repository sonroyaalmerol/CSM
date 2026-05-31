using CSM.API.Commands;
using ProtoBuf;

namespace CSM.Commands.Data.Sync
{
    /// <summary>
    ///     Bidirectional state hash exchange. Both server and clients
    ///     send their FNV-1a 64-bit hash of the current game state.
    ///     Receivers compare against their own hash to detect desync.
    /// </summary>
    [ProtoContract]
    public class StateHashCommand : CommandBase
    {
        [ProtoMember(1)]
        public uint Tick { get; set; }

        [ProtoMember(2)]
        public ulong Hash { get; set; }

        [ProtoMember(3)]
        public int SenderId { get; set; }
    }
}
