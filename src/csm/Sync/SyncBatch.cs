using System;

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
    ///     │     0 = COMMAND_BATCH (reserved, not yet used)  │
    ///     │     1 = TICK_SYNC    (server→client)            │
    ///     │     2 = STATE_HASH   (bidirectional)            │
    ///     │   bit  3:   has_target_tick                     │
    ///     │   bit  4:   has_sender_id                       │
    ///     │   bit  5:   has_pipeline_depth                  │
    ///     │   bit  6:   reserved                            │
    ///     │   bit  7:   reserved                            │
    ///     ├─────────────────────────────────────────────────┤
    ///     │ [varint] target tick    (if has_target_tick)     │
    ///     │ [varint] sender id      (if has_sender_id)      │
    ///     │ [varint] pipeline depth (if has_pipeline_depth)  │
    ///     ├─────────────────────────────────────────────────┤
    ///     │ Per-type payload (see below)                     │
    ///     └─────────────────────────────────────────────────┘
    ///
    ///     TICK_SYNC payload:
    ///       [varint] server current tick
    ///
    ///     STATE_HASH payload:
    ///       [varint] tick number
    ///       [8 bytes] FNV-1a 64-bit hash
    ///
    ///     COMMAND_BATCH is reserved for future use where the server
    ///     may relay multiple commands for a tick in a single envelope.
    ///     Currently all commands are relayed individually with an
    ///     authoritative TargetFrameIndex stamped by the server.
    /// </summary>
    public static class SyncBatch
    {
        // ── Packet type constants ──────────────────────────
        public const byte TYPE_COMMAND_BATCH = 0; // Reserved
        public const byte TYPE_TICK_SYNC = 1;
        public const byte TYPE_STATE_HASH = 2;

        // ── Header flag masks ──────────────────────────────
        private const byte FLAG_TYPE_MASK = 0x07;
        private const byte FLAG_HAS_TARGET_TICK = 0x08;
        private const byte FLAG_HAS_SENDER_ID = 0x10;
        private const byte FLAG_HAS_PIPELINE_DEPTH = 0x20;

        // Pooled writer to avoid allocation per packet.
        private static readonly SyncWriter _pooledWriter = new SyncWriter(256);

        // ── Build: TICK_SYNC (server → clients) ────────────

        /// <summary>
        ///     Build a TICK_SYNC packet. Sent by the server periodically
        ///     to tell clients how far they're allowed to advance.
        ///     Total size: typically 3-6 bytes.
        /// </summary>
        public static byte[] BuildTickSync(uint serverTick, uint pipelineDepth)
        {
            var w = _pooledWriter;
            w.Reset();
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
            var w = _pooledWriter;
            w.Reset();
            byte header = TYPE_STATE_HASH | FLAG_HAS_TARGET_TICK | FLAG_HAS_SENDER_ID;
            w.WriteByte(header);
            w.WriteVarInt(tick);
            w.WriteZigZag(senderId);
            w.WriteBytes(BitConverter.GetBytes(hash), 0, 8);
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
            }

            return result;
        }

        // ── Data structures ────────────────────────────────

        /// <summary>A parsed sync packet.</summary>
        public class ParsedPacket
        {
            public byte Type;
            public uint TargetTick;
            public int SenderId;
            public uint PipelineDepth;

            // TICK_SYNC
            public uint ServerTick;

            // STATE_HASH
            public uint HashTick;
            public ulong StateHash;
        }

        // ── Header inspection ──────────────────────────────

        /// <summary>Quickly determine the type of a sync packet (read 1 byte, no allocation).</summary>
        public static byte PeekType(byte[] data)
        {
            return (byte)(data[0] & FLAG_TYPE_MASK);
        }
    }
}
