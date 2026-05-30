using System;
using CSM.API.Networking;

namespace CSM.API.Commands
{
    public abstract class CommandHandler
    {
        /// <summary>
        ///     If this is true, client -> server packets are relayed to all other clients.
        /// </summary>
        public bool RelayOnServer { get; protected set; } = true;

        /// <summary>
        ///     If this is true, this command is only executed after the FinishTransactionCommand is received.
        /// </summary>
        public bool TransactionCmd { get; protected set; } = true;

        /// <summary>
        ///     If true, uses ReliableSequenced delivery instead of ReliableOrdered.
        ///     ReliableSequenced avoids head-of-line blocking on packet loss by dropping
        ///     stale packets instead of buffering them. Use for commands where only the
        ///     latest state matters (cursor positions, slowdown, etc.).
        /// </summary>
        public bool UseSequencedDelivery { get; protected set; } = false;

        public abstract Type GetDataType();

        public abstract void Parse(CommandBase message);

        public virtual void OnClientConnect(Player player)
        {
        }

        public virtual void OnClientDisconnect(Player player)
        {
        }
    }

    public abstract class CommandHandler<C> : CommandHandler where C : CommandBase
    {
        protected abstract void Handle(C command);

        public override Type GetDataType()
        {
            return typeof(C);
        }

        public override void Parse(CommandBase command)
        {
            Handle((C) command);
        }
    }
}
