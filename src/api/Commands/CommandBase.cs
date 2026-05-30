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
        ///     The id of the sending player. -1 for the server.
        /// </summary>
        [ProtoMember(1)]
        public int SenderId { get; set; }

        /// <summary>
        ///     The simulation tick at which this command should be executed.
        ///     Assigned by the server during relay. 0 = execute immediately
        ///     (used for non-tick-synced meta-commands).
        /// </summary>
        [ProtoMember(2)]
        public uint TargetFrameIndex { get; set; }
    }
}
