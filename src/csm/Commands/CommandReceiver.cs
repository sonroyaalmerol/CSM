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
        /// <param name="cmd">The deserialized command (needed by server for relay stamping).</param>
        /// <returns>If the command should be forwarded to other clients.</returns>
        public static bool Parse(NetPacketReader reader, NetPeer peer, out bool useSequenced, out CommandBase cmd)
        {
            Parse(reader, out CommandHandler handler, out cmd);

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
        ///     Parse a sync envelope packet (TICK_SYNC, STATE_HASH).
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
            }
        }

        // ── Shared command routing ─────────────────────────

        /// <summary>
        ///     Route a command through the tick-sync buffer or immediate execution.
        ///     On the server, tick-synced commands always execute immediately
        ///     (the server is the authority and assigns target ticks during relay).
        ///     On the client, tick-synced commands are buffered until their target tick.
        ///     Non-tick-synced commands go through the transaction handler or execute directly.
        /// </summary>
        internal static void RouteCommand(CommandHandler handler, CommandBase cmd)
        {
            if (handler.RequiresTickSync && TickClock.IsInitialized)
            {
                // Server executes tick-synced commands immediately — it's the authority.
                if (MultiplayerManager.Instance.CurrentRole == MultiplayerRole.Server)
                {
                    // Fall through to transaction check / immediate execution.
                }
                else
                {
                    // Client: buffer for the target tick assigned by the server.
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
                DesyncDetector.OnHashMismatch(pkt.HashTick, ourHash, pkt.StateHash, pkt.SenderId);
            }
            else
            {
                Log.Debug($"[Sync] STATE_HASH verified at tick {pkt.HashTick}");
                DesyncDetector.OnHashMatch(pkt.HashTick);
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
