using System;
using CSM.API;

namespace CSM.Sync
{
    /// <summary>
    ///     Wire format for the sync protocol.
    ///     Every byte is designed; no padding, no waste.
    ///
    ///     Packet layout:
    ///     ┌─────────────────────────────────────────────────┐
    ///     │ [1 byte] Magic marker 0xFE                       │
    ///     │   Protobuf field tags never produce 0xFE as the  │
    ///     │   first byte (wire type 6 is reserved/invalid),  │
    ///     │   so this is an unambiguous discriminator.       │
    ///     ├─────────────────────────────────────────────────┤
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
    ///
    ///     Discriminator rationale:
    ///       The magic byte 0xFE is chosen because protobuf field tags
    ///       encode as (field_number << 3 | wire_type). Wire types are
    ///       0-5. For a single-byte tag (field 1-15), the range is
    ///       0x08-0x7D. Multi-byte tags start at 0x80. 0xFE as a tag
    ///       would decode as wire type 6 (0xFE & 0x07 = 6), which is
    ///       reserved and never produced by any valid protobuf encoder.
    ///
    ///     Thread safety: all SyncWriter/SyncReader operations are expected to
    ///     run on the main Unity thread (LiteNetLib polls inline in
    ///     ThreadingExtension.OnUpdate). The pooled writer uses a runtime
    ///     assertion to catch misuse if called from a background thread.
    /// </summary>
    public static class SyncBatch
    {
        // ── Magic byte ─────────────────────────────────────
        // Protobuf never produces 0xFE as a first byte because wire type 6
        // is reserved/invalid in the protobuf encoding spec.
        public const byte SYNC_MAGIC = 0xFE;

        // ── Packet type constants ──────────────────────────
        public const byte TYPE_TICK_SYNC = 1;
        public const byte TYPE_STATE_HASH = 2;

        // ── Header flag masks ──────────────────────────────
        private const byte FLAG_TYPE_MASK = 0x07;
        private const byte FLAG_HAS_TARGET_TICK = 0x08;
        private const byte FLAG_HAS_SENDER_ID = 0x10;
        private const byte FLAG_HAS_PIPELINE_DEPTH = 0x20;

        // Pooled writer to avoid allocation per packet.
        // Thread safety: all callers run on the main thread. The assertion
        // below catches accidental cross-thread use during development.
        private static readonly SyncWriter _pooledWriter = new SyncWriter(256);

        // Expected thread ID captured at class init. Used for debug assertions
        // to catch accidental use from background threads.
        private static readonly int _ownerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        /// <summary>
        ///     Check whether a raw byte array is a sync protocol packet.
        ///     Uses the 0xFE magic byte for unambiguous identification.
        ///     Safe to call on any incoming packet before attempting protobuf parse.
        /// </summary>
        public static bool IsSyncPacket(byte[] data)
        {
            return data != null && data.Length > 0 && data[0] == SYNC_MAGIC;
        }

        // ── Build: TICK_SYNC (server → clients) ────────────

        /// <summary>
        ///     Build a TICK_SYNC packet. Sent by the server periodically
        ///     to tell clients how far they're allowed to advance.
        ///     Total size: typically 4-7 bytes.
        /// </summary>
        public static byte[] BuildTickSync(uint serverTick, uint pipelineDepth)
        {
            AssertMainThread();
            var w = _pooledWriter;
            w.Reset();
            w.WriteByte(SYNC_MAGIC);
            byte header = TYPE_TICK_SYNC | FLAG_HAS_PIPELINE_DEPTH;
            w.WriteByte(header);
            w.WriteVarInt(serverTick);
            w.WriteVarInt(pipelineDepth);
            return w.ToArray();
        }

        // ── Build: STATE_HASH (bidirectional) ──────────────

        /// <summary>
        ///     Build a STATE_HASH packet. Sent every ~1 second (time-based interval).
        ///     Total size: typically 12-14 bytes.
        /// </summary>
        public static byte[] BuildStateHash(uint tick, ulong hash, int senderId)
        {
            AssertMainThread();
            var w = _pooledWriter;
            w.Reset();
            w.WriteByte(SYNC_MAGIC);
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
        ///     Expects the 0xFE magic byte to be present at position 0.
        /// </summary>
        public static ParsedPacket Parse(byte[] data)
        {
            var r = new SyncReader(data);
            var result = new ParsedPacket();

            // Skip magic byte
            byte magic = r.ReadByte();
            if (magic != SYNC_MAGIC)
            {
                Log.Error($"[SyncBatch] Invalid magic byte: 0x{magic:X2}, expected 0x{SYNC_MAGIC:X2}");
                return result;
            }

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

        /// <summary>
        ///     Quickly determine the type of a sync packet (reads header byte
        ///     at index 1, skipping magic byte). No allocation.
        /// </summary>
        public static byte PeekType(byte[] data)
        {
            if (data == null || data.Length < 2 || data[0] != SYNC_MAGIC)
                return 0xFF; // Invalid
            return (byte)(data[1] & FLAG_TYPE_MASK);
        }

        // ── Thread safety assertion ───────────────────────

        /// <summary>
        ///     Asserts that the current thread is the same thread that
        ///     initialized the class. The pooled writer is not thread-safe
        ///     by design — all network processing runs on the main thread.
        ///     This catches accidental use from background threads.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void AssertMainThread()
        {
            int current = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (current != _ownerThreadId)
            {
                Log.Error($"[SyncBatch] Thread safety violation: pooled writer accessed " +
                          $"from thread {current}, expected {_ownerThreadId}. " +
                          $"All sync packet building must happen on the main thread.");
            }
        }
    }
}
