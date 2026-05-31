using CSM.API;
using CSM.API.Commands;
using CSM.Commands.Data.Sync;
using CSM.Sync;

namespace CSM.Commands.Handler.Sync
{
    public class StateHashHandler : CommandHandler<StateHashCommand>
    {
        public StateHashHandler()
        {
            TransactionCmd = false;
            RelayOnServer = false;
            RequiresTickSync = false;
        }

        protected override void Handle(StateHashCommand command)
        {
            ulong ourHash = StateHasher.ComputeHash();

            if (ourHash != command.Hash)
            {
                Log.Warn($"[Sync] STATE_HASH MISMATCH at tick {command.Tick}: " +
                         $"ours=0x{ourHash:X16}, theirs=0x{command.Hash:X16}, " +
                         $"sender={command.SenderId}");
                DesyncDetector.OnHashMismatch(command.Tick, ourHash, command.Hash, command.SenderId);
            }
            else
            {
                Log.Debug($"[Sync] STATE_HASH verified at tick {command.Tick}");
                DesyncDetector.OnHashMatch(command.Tick);
            }
        }
    }
}
