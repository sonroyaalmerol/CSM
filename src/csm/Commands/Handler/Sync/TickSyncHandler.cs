using CSM.API;
using CSM.API.Commands;
using CSM.Commands.Data.Sync;
using CSM.Networking;
using CSM.Sync;

namespace CSM.Commands.Handler.Sync
{
    public class TickSyncHandler : CommandHandler<TickSyncCommand>
    {
        public TickSyncHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
            RequiresTickSync = false;
            UseSequencedDelivery = true;
        }

        protected override void Handle(TickSyncCommand command)
        {
            TickClock.OnServerTick(command.ServerTick, command.PipelineDepth);
            Log.Debug($"[Sync] TICK_SYNC: serverTick={command.ServerTick}, " +
                      $"pipeline={command.PipelineDepth}, maxAllowed={TickClock.MaxAllowedTick}");
        }
    }
}
