using ProtoBuf;

namespace CSM.API.Commands
{
    /// <summary>
    ///     A base protobuf command that all other commands in this mod should
    ///     extend. Provides support for serialization.
    ///
    ///     When creating new commands, you should create a new command ID (up to 255) which
    ///     represents this command when sending over the network.
    /// </summary>
    [ProtoContract]
    public abstract class CommandBase
    {
        /// <summary>
        ///     Protocol version for the tick-sync extension. Bumped when the
        ///     wire format or sync behavior changes incompatibly.
        ///     Checked during connection handshake to prevent forked/vanilla
        ///     client mismatches.
        /// </summary>
        public const int SyncProtocolVersion = 2;
        /// <summary>
        ///     The id of the sending player. -1 for the server.
        /// </summary>
        [ProtoMember(1)]
        public int SenderId { get; set; }

        /// <summary>
        ///     The simulation tick at which this command should be executed.
        ///     Assigned by the server during relay. 0 = execute immediately
        ///     (used for non-tick-synced meta-commands).
        ///
        ///     Backward compatibility: [ProtoMember(2)] is additive — vanilla CSM
        ///     clients that don't have this field will silently default to 0,
        ///     which causes tick-synced commands to execute immediately.
        ///     This is why the protocol version handshake exists: vanilla clients
        ///     are rejected at connection time with a clear error message, so they
        ///     never reach a state where TargetFrameIndex=0 would cause problems.
        /// </summary>
        [ProtoMember(2)]
        public uint TargetFrameIndex { get; set; }

        /// <summary>
        ///     Monotonic sequence counter per sender, used for deduplication
        ///     of redundant retransmissions in the command buffer. Each send
        ///     increments the counter, so two different commands from the
        ///     same sender will have different SendSeq values, while a
        ///     retransmitted copy will have the same SendSeq.
        ///     0 = not assigned (treated as unique, never deduped).
        /// </summary>
        [ProtoMember(3)]
        public uint SendSeq { get; set; }
    }
}
