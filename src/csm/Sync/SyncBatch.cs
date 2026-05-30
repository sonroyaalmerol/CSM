using System;
using System.Collections.Generic;
using CSM.API;
using CSM.API.Commands;
using CSM.Networking;

namespace CSM.Sync
{
    /// <summary>
    ///     Wire format for the sync protocol.
    ///     Every byte is designed; no padding, no waste.
    ///
    ///     Packet layout:
    ///     ┌─────────────────────────────────────────────────┐
    ///     │ [1 byte] Header flags                            │
    ///     │   bits 0-2: packet type                          │
    ///     │     0 = COMMAND_BATCH (client→server or relay)  │
    ///     │     1 = TICK_SYNC    (server→client)            │
    ///     │     2 = STATE_HASH   (bidirectional)            │
    ///     │   bit  3:   has_target_tick                     │
    ///     │   bit  4:   has_sender_id                       │
    ///     │   bit  5:   has_pipeline_depth                  │
    ///     │   bit  6:   is_last_batch_in_tick               │
    ///     │   bit  7:   reserved                            │
    ///     ├─────────────────────────────────────────────────┤
    ///     │ [varint] target tick    (if has_target_tick)     │
    ///     │ [varint] sender id      (if has_sender_id)      │
    ///     │ [varint] pipeline depth (if has_pipeline_depth)  │
    ///     ├─────────────────────────────────────────────────┤
    ///     │ Per-type payload (see below)                     │
    ///     └─────────────────────────────────────────────────┘
    ///
    ///     COMMAND_BATCH payload:
    ///       [varint] command count
    ///       Per command:
    ///         [varint] command type id (0-255, assigned by registration order)
    ///         [varint] payload byte length
    ///         [bytes]  protobuf-encoded command data
    ///
    ///     TICK_SYNC payload:
    ///       [varint] server current tick
    ///
    ///     STATE_HASH payload:
    ///       [varint] tick number
    ///       [8 bytes] FNV-1a 64-bit hash
    /// </summary>
    public static class SyncBatch
    {
        // ── Packet type constants ──────────────────────────
        public const byte TYPE_COMMAND_BATCH = 0;
        public const byte TYPE_TICK_SYNC = 1;
        public const byte TYPE_STATE_HASH = 2;

        // ── Header flag masks ──────────────────────────────
        private const byte FLAG_TYPE_MASK = 0x07;
        private const byte FLAG_HAS_TARGET_TICK = 0x08;
        private const byte FLAG_HAS_SENDER_ID = 0x10;
        private const byte FLAG_HAS_PIPELINE_DEPTH = 0x20;
        private const byte FLAG_LAST_BATCH = 0x40;

        // ── Build: TICK_SYNC (server → clients) ────────────

        /// <summary>
        ///     Build a TICK_SYNC packet. Sent by the server periodically
        ///     to tell clients how far they're allowed to advance.
        ///     Total size: typically 3-6 bytes.
        /// </summary>
        public static byte[] BuildTickSync(uint serverTick, uint pipelineDepth)
        {
            var w = new SyncWriter(16);
            // Header: type=1, has_pipeline_depth=1
            byte header = TYPE_TICK_SYNC | FLAG_HAS_PIPELINE_DEPTH;
            w.WriteByte(header);
            w.WriteVarInt(serverTick);
            w.WriteVarInt(pipelineDepth);
            return w.ToArray();
        }

        // ── Build: STATE_HASH (bidirectional) ──────────────

        /// <summary>
        ///     Build a STATE_HASH packet. Sent every HashInterval ticks.
        ///     Total size: typically 11-13 bytes.
        /// </summary>
        public static byte[] BuildStateHash(uint tick, ulong hash, int senderId)
        {
            var w = new SyncWriter(24);
            byte header = TYPE_STATE_HASH | FLAG_HAS_TARGET_TICK | FLAG_HAS_SENDER_ID;
            w.WriteByte(header);
            w.WriteVarInt(tick);
            w.WriteZigZag(senderId);
            w.WriteBytes(BitConverter.GetBytes(hash), 0, 8);
            return w.ToArray();
        }

        // ── Build: COMMAND_BATCH (client→server or server→client relay) ──

        /// <summary>
        ///     Build a COMMAND_BATCH envelope wrapping one or more serialized commands.
        ///     Each command payload is already protobuf-serialized.
        ///     The envelope adds only ~5 bytes of overhead per command.
        /// </summary>
        public static byte[] BuildCommandBatch(uint targetTick, int senderId,
            List<CommandEntry> commands, bool isLastBatch)
        {
            // Estimate: ~10 bytes header + ~10 bytes per command overhead + payloads
            int estSize = 16 + commands.Count * 10;
            foreach (var cmd in commands) estSize += cmd.Payload.Length;
            var w = new SyncWriter(estSize);

            // Header
            byte header = TYPE_COMMAND_BATCH | FLAG_HAS_TARGET_TICK | FLAG_HAS_SENDER_ID;
            if (isLastBatch) header |= FLAG_LAST_BATCH;
            w.WriteByte(header);

            // Tick and sender
            w.WriteVarInt(targetTick);
            w.WriteZigZag(senderId);

            // Command count
            w.WriteVarInt((uint)commands.Count);

            // Commands
            foreach (var cmd in commands)
            {
                w.WriteVarInt(cmd.TypeId);
                w.WriteVarInt((uint)cmd.Payload.Length);
                w.WriteBytes(cmd.Payload, 0, cmd.Payload.Length);
            }

            return w.ToArray();
        }

        // ── Parse ──────────────────────────────────────────

        /// <summary>
        ///     Parse a sync packet. Returns a ParsedPacket with the type and data.
        /// </summary>
        public static ParsedPacket Parse(byte[] data)
        {
            var r = new SyncReader(data);
            var result = new ParsedPacket();

            // Header byte
            byte header = r.ReadByte();
            byte type = (byte)(header & FLAG_TYPE_MASK);
            result.Type = type;

            if ((header & FLAG_HAS_TARGET_TICK) != 0)
                result.TargetTick = r.ReadVarInt();

            if ((header & FLAG_HAS_SENDER_ID) != 0)
                result.SenderId = r.ReadZigZag();

            if ((header & FLAG_HAS_PIPELINE_DEPTH) != 0)
                result.PipelineDepth = r.ReadVarInt();

            result.IsLastBatch = (header & FLAG_LAST_BATCH) != 0;

            // Parse type-specific payload
            switch (type)
            {
                case TYPE_TICK_SYNC:
                    result.ServerTick = r.ReadVarInt();
                    break;

                case TYPE_STATE_HASH:
                    result.HashTick = r.ReadVarInt();
                    result.StateHash = BitConverter.ToUInt64(r.ReadBytes(8), 0);
                    break;

                case TYPE_COMMAND_BATCH:
                    uint count = r.ReadVarInt();
                    result.Commands = new List<ParsedCommand>((int)count);
                    for (int i = 0; i < count; i++)
                    {
                        var cmd = new ParsedCommand
                        {
                            TypeId = r.ReadVarInt(),
                            PayloadLength = r.ReadVarInt()
                        };
                        cmd.Payload = r.ReadBytes((int)cmd.PayloadLength);
                        result.Commands.Add(cmd);
                    }
                    break;
            }

            return result;
        }

        // ── Data structures ────────────────────────────────

        /// <summary>A command awaiting envelope wrapping.</summary>
        public struct CommandEntry
        {
            /// <summary>Registration-order command type ID from CommandInternal.</summary>
            public uint TypeId;
            /// <summary>Protobuf-serialized command payload.</summary>
            public byte[] Payload;
        }

        /// <summary>A parsed sync packet.</summary>
        public class ParsedPacket
        {
            public byte Type;
            public uint TargetTick;
            public int SenderId;
            public uint PipelineDepth;
            public bool IsLastBatch;

            // TICK_SYNC
            public uint ServerTick;

            // STATE_HASH
            public uint HashTick;
            public ulong StateHash;

            // COMMAND_BATCH
            public List<ParsedCommand> Commands;
        }

        /// <summary>A single parsed command from a batch.</summary>
        public struct ParsedCommand
        {
            public uint TypeId;
            public uint PayloadLength;
            public byte[] Payload;
        }

        // ── Header inspection ──────────────────────────────

        /// <summary>Quickly determine the type of a sync packet (read 1 byte, no allocation).</summary>
        public static byte PeekType(byte[] data)
        {
            return (byte)(data[0] & FLAG_TYPE_MASK);
        }
    }
}
