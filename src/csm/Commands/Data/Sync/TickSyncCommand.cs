using CSM.API.Commands;
using ProtoBuf;

namespace CSM.Commands.Data.Sync
{
    /// <summary>
    ///     Sent by the server periodically to tell clients the current
    ///     server tick and pipeline depth. Clients must not advance
    ///     their simulation past serverTick + pipelineDepth.
    /// </summary>
    [ProtoContract]
    public class TickSyncCommand : CommandBase
    {
        [ProtoMember(1)]
        public uint ServerTick { get; set; }

        [ProtoMember(2)]
        public uint PipelineDepth { get; set; }
    }
}
