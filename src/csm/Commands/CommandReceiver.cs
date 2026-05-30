using System.IO;
using CSM.API;
using CSM.API.Commands;
using CSM.Commands.Data.Internal;
using CSM.Commands.Handler.Internal;
using CSM.Networking;
using CSM.Sync;
using LiteNetLib;

namespace CSM.Commands
{
    public static class CommandReceiver
    {
        /// <summary>
        ///     Parse an incoming message on the client/server
        ///     and execute the appropriate actions.
        /// </summary>
        /// <param name="reader">The incoming packet including the command type byte.</param>
        /// <param name="peer">The peer object of the sending client.</param>
        /// <param name="useSequenced">Whether the relay should use ReliableSequenced delivery.</param>
        /// <returns>If the command should be forwarded to other clients.</returns>
        public static bool Parse(NetPacketReader reader, NetPeer peer, out bool useSequenced)
        {
            Parse(reader, out CommandHandler handler, out CommandBase cmd);

            useSequenced = false;

            if (handler == null)
            {
                return false;
            }

            useSequenced = handler.UseSequencedDelivery;

            // Handle connection request as special case
            if (cmd.GetType() == typeof(ConnectionRequestCommand))
            {
                ((ConnectionRequestHandler)handler).HandleOnServer((ConnectionRequestCommand)cmd, peer);
                return false;
            }

            // Make sure we know about the connected client on the server
            if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server && !MultiplayerManager.Instance.CurrentServer.ConnectedPlayers.ContainsKey(peer.Id))
            {
                Log.Warn("Client tried to send packet but never joined with a ConnectionRequestCommand. Ignoring...");
                return false;
            }

            RouteCommand(handler, cmd);
            return handler.RelayOnServer;
        }

        /// <summary>
        ///     Parse a sync envelope packet (TICK_SYNC, STATE_HASH, COMMAND_BATCH).
        /// </summary>
        public static void ParseSyncPacket(byte[] data)
        {
            byte type = SyncBatch.PeekType(data);

            switch (type)
            {
                case SyncBatch.TYPE_TICK_SYNC:
                    HandleTickSync(data);
                    break;

                case SyncBatch.TYPE_STATE_HASH:
                    HandleStateHash(data);
                    break;

                case SyncBatch.TYPE_COMMAND_BATCH:
                    HandleCommandBatch(data);
                    break;
            }
        }

        // ── Shared command routing (used by Parse and HandleCommandBatch) ──

        /// <summary>
        ///     Route a command through the tick-sync buffer or immediate execution.
        ///     Tick-synced commands are buffered for their target tick;
        ///     all others go through the transaction handler or execute directly.
        /// </summary>
        private static void RouteCommand(CommandHandler handler, CommandBase cmd)
        {
            if (handler.RequiresTickSync && TickClock.IsInitialized)
            {
                uint targetTick = cmd.TargetFrameIndex;

                // If target tick is 0, it wasn't assigned (direct protobuf path).
                // Assign a reasonable default: current tick + pipeline depth.
                if (targetTick == 0)
                {
                    targetTick = TickClock.LocalTick + TickClock.PipelineDepth;
                }

                bool buffered = CommandBuffer.Buffer(targetTick, handler, cmd, TickClock.LocalTick);
                if (buffered)
                    return;

                // Target tick already passed — fall through to immediate execution.
            }

            // Transaction path or immediate execution
            if (TransactionHandler.CheckReceived(handler, cmd))
                return;

            handler.Parse(cmd);
        }

        // ── Sync packet handlers ───────────────────────────

        private static void HandleTickSync(byte[] data)
        {
            var pkt = SyncBatch.Parse(data);
            TickClock.OnServerTick(pkt.ServerTick, pkt.PipelineDepth);
            Log.Debug($"[Sync] TICK_SYNC: serverTick={pkt.ServerTick}, " +
                      $"pipeline={pkt.PipelineDepth}, maxAllowed={TickClock.MaxAllowedTick}");
        }

        private static void HandleStateHash(byte[] data)
        {
            var pkt = SyncBatch.Parse(data);

            ulong ourHash = StateHasher.ComputeHash();

            if (ourHash != pkt.StateHash)
            {
                Log.Warn($"[Sync] STATE_HASH MISMATCH at tick {pkt.HashTick}: " +
                         $"ours=0x{ourHash:X16}, theirs=0x{pkt.StateHash:X16}, " +
                         $"sender={pkt.SenderId}");
            }
            else
            {
                Log.Debug($"[Sync] STATE_HASH verified at tick {pkt.HashTick}");
            }
        }

        private static void HandleCommandBatch(byte[] data)
        {
            var pkt = SyncBatch.Parse(data);

            foreach (var raw in pkt.Commands)
            {
                CommandBase cmd = Deserialize(raw.Payload);
                if (cmd == null) continue;

                cmd.SenderId = pkt.SenderId;
                cmd.TargetFrameIndex = pkt.TargetTick;

                CommandHandler handler = CommandInternal.Instance.GetCommandHandler(cmd.GetType());
                if (handler == null) continue;

                RouteCommand(handler, cmd);
            }

            if (pkt.IsLastBatch && pkt.TargetTick > 0)
            {
                CommandBuffer.MarkComplete(pkt.TargetTick);
            }
        }

        // ── Deserialization ────────────────────────────────

        private static void Parse(NetPacketReader reader, out CommandHandler handler, out CommandBase cmd)
        {
            cmd = Deserialize(reader.GetRemainingBytes());

            Log.Debug($"Received {cmd.GetType().Name}");

            handler = CommandInternal.Instance.GetCommandHandler(cmd.GetType());
            if (handler == null)
            {
                Log.Error($"Command {cmd.GetType().Name} not found!");
                return;
            }
        }

        /// <summary>
        ///     Deserialize the command from a byte array.
        /// </summary>
        public static CommandBase Deserialize(byte[] message)
        {
            using (MemoryStream stream = new MemoryStream(message))
            {
                return (CommandBase)CommandInternal.Instance.Model.Deserialize(stream, null, typeof(CommandBase));
            }
        }
    }
}
