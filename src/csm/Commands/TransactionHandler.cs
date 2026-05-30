using System;
using System.Collections.Generic;
using CSM.API;
using CSM.API.Commands;
using CSM.Commands.Data.Internal;

namespace CSM.Commands
{
    public static class TransactionHandler
    {
        /// <summary>
        ///     Maximum number of simulation ticks a transaction can remain queued
        ///     before it is considered stale and cleaned up.
        /// </summary>
        private const int MaxTransactionAge = 600; // ~10 seconds at 60 ticks/sec

        private static bool _sendStarted = false;
        private static readonly List<PendingTransaction> _receivedTransactions = new List<PendingTransaction>();
        private static int _tickCounter = 0;

        private class PendingTransaction
        {
            public CommandHandler Handler;
            public CommandBase Command;
            public int SenderId;
            public int QueuedAtTick;
        }

        /// <summary>
        /// Starts a transaction.
        /// </summary>
        public static void StartTransaction()
        {
            _sendStarted = true;
        }

        /// <summary>
        /// Starts a transaction if the command is not a finish command.
        /// </summary>
        /// <param name="command">A received command</param>
        public static void StartTransaction(CommandBase command)
        {
            if (CommandInternal.Instance.GetCommandHandler(command.GetType()).TransactionCmd)
            {
                _sendStarted = true;
            }
        }

        /// <summary>
        /// Finishes all transactions that were started before.
        /// Also performs per-tick cleanup of stale transactions.
        /// </summary>
        public static void FinishSend()
        {
            _tickCounter++;

            // Clean up stale transactions that never received a FinishTransactionCommand
            if (_tickCounter % 60 == 0)
            {
                int removed = _receivedTransactions.RemoveAll(t => _tickCounter - t.QueuedAtTick > MaxTransactionAge);
                if (removed > 0)
                {
                    Log.Warn($"Cleaned up {removed} stale transactions that timed out without a FinishTransactionCommand.");
                }
            }

            if (_sendStarted)
            {
                CommandInternal.Instance.SendToAll(new FinishTransactionCommand());
                _sendStarted = false;
            }
        }

        /// <summary>
        /// Checks if a received command is a transaction command
        /// and queues it until a FinishTransactionCommand is received.
        /// </summary>
        /// <param name="handler">The received command type.</param>
        /// <param name="cmd">The received command.</param>
        /// <returns>true, if the command is a transaction command</returns>
        public static bool CheckReceived(CommandHandler handler, CommandBase cmd)
        {
            if (!handler.TransactionCmd)
            {
                return false;
            }

            _receivedTransactions.Add(new PendingTransaction
            {
                Handler = handler,
                Command = cmd,
                SenderId = cmd.SenderId,
                QueuedAtTick = _tickCounter
            });

            return true;
        }

        /// <summary>
        /// Called when the FinishTransactionCommand was received.
        /// </summary>
        /// <param name="sender">The sending player, -1 if it's the server.</param>
        public static void FinishReceived(int sender)
        {
            // Process from the end to allow removal during iteration
            for (int i = _receivedTransactions.Count - 1; i >= 0; i--)
            {
                PendingTransaction transaction = _receivedTransactions[i];
                if (transaction.SenderId != sender)
                {
                    continue;
                }

                try
                {
                    transaction.Handler.Parse(transaction.Command);
                }
                catch (Exception ex)
                {
                    Log.Error($"Exception while parsing {transaction.Command.GetType().Name}", ex);
                }

                _receivedTransactions.RemoveAt(i);
            }
        }

        /// <summary>
        /// Clears all transactions by the given sender.
        /// </summary>
        /// <param name="clientId">The sender's client id.</param>
        public static void ClearTransactions(int clientId)
        {
            _receivedTransactions.RemoveAll(t => t.SenderId == clientId);
        }

        /// <summary>
        /// Clears all transactions.
        /// </summary>
        public static void ClearTransactions()
        {
            _sendStarted = false;
            _receivedTransactions.Clear();
        }
    }
}
