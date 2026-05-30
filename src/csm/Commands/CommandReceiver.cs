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
        ///     This method is used to parse an incoming message on the client
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

            // ── Tick-sync buffer path ──────────────────────
            // If this command requires tick sync and we're connected,
            // buffer it for execution at the assigned target tick.
            if (handler.RequiresTickSync && TickClock.IsInitialized)
            {
                uint targetTick = cmd.TargetFrameIndex;

                // If target tick is 0, it wasn't assigned yet (direct protobuf path).
                // Assign a reasonable default: current tick + pipeline depth.
                if (targetTick == 0)
                {
                    targetTick = TickClock.LocalTick + TickClock.PipelineDepth;
                }

                bool buffered = CommandBuffer.Buffer(targetTick, handler, cmd, TickClock.LocalTick);

                if (buffered)
                {
                    // Command was buffered for future execution. Still relay if needed.
                    return handler.RelayOnServer;
                }
                // Target tick already passed — fall through to immediate execution.
                // This handles the case where the command arrived late.
            }

            // ── Transaction path (non-tick-synced commands) ──
            if (TransactionHandler.CheckReceived(handler, cmd))
            {
                return handler.RelayOnServer;
            }

            // ── Immediate execution ────────────────────────
            handler.Parse(cmd);

            return handler.RelayOnServer;
        }

        /// <summary>
        ///     Parse a sync envelope packet. Used by the new SyncBatch wire format.
        ///     Handles TICK_SYNC, STATE_HASH, and COMMAND_BATCH packets.
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

            // Compute our own hash at the same tick
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
                // Deserialize the protobuf payload
                CommandBase cmd = Deserialize(raw.Payload);
                if (cmd == null) continue;

                cmd.SenderId = pkt.SenderId;
                cmd.TargetFrameIndex = pkt.TargetTick;

                CommandHandler handler = CommandInternal.Instance.GetCommandHandler(cmd.GetType());
                if (handler == null) continue;

                // Buffer or execute based on tick-sync requirement
                if (handler.RequiresTickSync && TickClock.IsInitialized)
                {
                    CommandBuffer.Buffer(pkt.TargetTick, handler, cmd, TickClock.LocalTick);
                }
                else
                {
                    // Non-tick-synced: execute immediately
                    if (TransactionHandler.CheckReceived(handler, cmd))
                        continue;
                    handler.Parse(cmd);
                }
            }

            // If this is the last batch for this tick, mark it complete
            if (pkt.IsLastBatch && pkt.TargetTick > 0)
            {
                CommandBuffer.MarkComplete(pkt.TargetTick);
            }
        }

        /// <summary>
        ///     This method is used to extract the command type from an incoming message
        ///     and return the matching handler object.
        /// </summary>
        /// <param name="reader">The incoming packet including the command type byte.</param>
        /// <param name="handler">This returns the command handler object. May be null if the command was not found.</param>
        /// <param name="cmd">This returns the command data object.</param>
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
        /// <param name="message">A byte array of the message</param>
        /// <returns>The deserialized command.</returns>
        public static CommandBase Deserialize(byte[] message)
        {
            CommandBase result;

            using (MemoryStream stream = new MemoryStream(message))
            {
                result = (CommandBase)CommandInternal.Instance.Model.Deserialize(stream, null, typeof(CommandBase));
            }

            return result;
        }
    }
}
