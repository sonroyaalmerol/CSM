using System;

namespace CSM.Sync
{
    /// <summary>
    ///     Bit-granularity binary writer. Every bit is accounted for.
    ///     No padding, no alignment, no wasted bytes.
    ///     Backed by a growable byte array reused across writes.
    /// </summary>
    public sealed class SyncWriter
    {
        private byte[] _buf;
        private int _len;       // total bytes written (byte-aligned)
        private int _bitPos;    // 0-7: next bit position in current byte
        private byte _cur;      // current byte being assembled

        public SyncWriter(int capacity = 256)
        {
            _buf = new byte[capacity];
            _len = 0;
            _bitPos = 0;
            _cur = 0;
        }

        // ── Bit-level ──────────────────────────────────────

        /// <summary>Write a single bit (LSB-first within byte).</summary>
        public void WriteBit(bool v)
        {
            if (v) _cur |= (byte)(1 << _bitPos);
            if (++_bitPos == 8) FlushByte();
        }

        /// <summary>Write N bits from value (LSB-first). Max N = 32.</summary>
        public void WriteBits(uint v, int n)
        {
            for (int i = 0; i < n; i++)
                WriteBit((v & (1u << i)) != 0);
        }

        /// <summary>Write an 8-bit bitfield from up to 8 bools.</summary>
        public void WriteBitField8(bool b0, bool b1 = false, bool b2 = false, bool b3 = false,
            bool b4 = false, bool b5 = false, bool b6 = false, bool b7 = false)
        {
            byte bits = 0;
            if (b0) bits |= 1;
            if (b1) bits |= 2;
            if (b2) bits |= 4;
            if (b3) bits |= 8;
            if (b4) bits |= 16;
            if (b5) bits |= 32;
            if (b6) bits |= 64;
            if (b7) bits |= 128;
            WriteByte(bits);
        }

        // ── Byte-aligned primitives ────────────────────────

        /// <summary>Align to next byte boundary (pad with zero bits).</summary>
        public void Align()
        {
            if (_bitPos != 0) FlushByte();
        }

        /// <summary>Write a raw byte. Auto-aligns first.</summary>
        public void WriteByte(byte v)
        {
            Align();
            EnsureCapacity(1);
            _buf[_len++] = v;
        }

        /// <summary>Write raw bytes. Auto-aligns first.</summary>
        public void WriteBytes(byte[] src, int offset, int count)
        {
            Align();
            EnsureCapacity(count);
            Buffer.BlockCopy(src, offset, _buf, _len, count);
            _len += count;
        }

        // ── LEB128 VarInt (unsigned) ───────────────────────
        // 1 byte: 0-127    2 bytes: 128-16383    3 bytes: 16384-2097151
        // Saves 1-3 bytes over fixed int32 for small values.

        public void WriteVarInt(uint v)
        {
            Align();
            while (v >= 0x80)
            {
                EnsureCapacity(1);
                _buf[_len++] = (byte)(v | 0x80);
                v >>= 7;
            }
            EnsureCapacity(1);
            _buf[_len++] = (byte)v;
        }

        /// <summary>Write a zigzag-encoded signed integer as VarInt.
        /// 0→0, -1→1, 1→2, -2→3, 2→4 ... </summary>
        public void WriteZigZag(int v)
        {
            WriteVarInt((uint)((v << 1) ^ (v >> 31)));
        }

        // ── Floats ─────────────────────────────────────────

        /// <summary>Full 32-bit IEEE 754 float.</summary>
        public void WriteFloat32(float v)
        {
            WriteBytes(BitConverter.GetBytes(v), 0, 4);
        }

        /// <summary>IEEE 754 half-precision (16-bit). Saves 2 bytes per value.
        /// Precision: ~3 decimal digits. Range: ±65504.</summary>
        public void WriteFloat16(float v)
        {
            Align();
            uint f = unchecked((uint)BitConverter.DoubleToInt64Bits((double)v));
            int sign = (int)(f >> 31) & 0x1;
            int exp = (int)((f >> 52) & 0x7FF);
            long mant = (long)(f & 0xFFFFFFFFFFFFF);

            int hSign = sign;
            int hExp, hMant;

            if (exp == 0x7FF)
            {
                // Inf / NaN
                hExp = 0x1F;
                hMant = (int)(mant != 0 ? 0x200 : 0);
            }
            else if (exp == 0)
            {
                hExp = 0;
                hMant = 0;
            }
            else
            {
                exp = exp - 1023 + 15;
                if (exp >= 0x1F)
                {
                    hExp = 0x1F;
                    hMant = 0;
                }
                else if (exp <= 0)
                {
                    hExp = 0;
                    hMant = 0;
                }
                else
                {
                    hExp = exp;
                    hMant = (int)(mant >> (52 - 10));
                }
            }

            ushort half = (ushort)((hSign << 15) | (hExp << 10) | (hMant & 0x3FF));
            EnsureCapacity(2);
            _buf[_len++] = (byte)(half & 0xFF);
            _buf[_len++] = (byte)((half >> 8) & 0xFF);
        }

        // ── Output ─────────────────────────────────────────

        /// <summary>Get the written data as a new byte array.</summary>
        public byte[] ToArray()
        {
            Align();
            byte[] result = new byte[_len];
            Buffer.BlockCopy(_buf, 0, result, 0, _len);
            return result;
        }

        /// <summary>Total bytes written (after alignment).</summary>
        public int Length
        {
            get { Align(); return _len; }
        }

        /// <summary>Reset writer for reuse (keeps allocated buffer).</summary>
        public void Reset()
        {
            _len = 0;
            _bitPos = 0;
            _cur = 0;
        }

        // ── Internals ──────────────────────────────────────

        private void FlushByte()
        {
            EnsureCapacity(1);
            _buf[_len++] = _cur;
            _cur = 0;
            _bitPos = 0;
        }

        private void EnsureCapacity(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int newSize = _buf.Length;
            while (newSize < _len + extra) newSize *= 2;
            byte[] newBuf = new byte[newSize];
            Buffer.BlockCopy(_buf, 0, newBuf, 0, _len);
            _buf = newBuf;
        }
    }

    /// <summary>
    ///     Bit-granularity binary reader. Mirror of SyncWriter.
    /// </summary>
    public sealed class SyncReader
    {
        private byte[] _buf;
        private int _pos;       // byte position
        private int _bitPos;    // 0-7: next bit within current byte
        private int _len;       // total bytes available

        public SyncReader(byte[] data) : this(data, 0, data.Length) { }
        public SyncReader(byte[] data, int offset, int count)
        {
            _buf = data;
            _pos = offset;
            _len = offset + count;
            _bitPos = 0;
        }

        // ── Bit-level ──────────────────────────────────────

        public bool ReadBit()
        {
            if (_pos >= _len) throw new InvalidOperationException("SyncReader: read past end");
            bool v = (_buf[_pos] & (1 << _bitPos)) != 0;
            if (++_bitPos == 8) { _pos++; _bitPos = 0; }
            return v;
        }

        public uint ReadBits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
                if (ReadBit()) v |= (1u << i);
            return v;
        }

        public void ReadBitField8(out bool b0, out bool b1, out bool b2, out bool b3,
            out bool b4, out bool b5, out bool b6, out bool b7)
        {
            byte bits = ReadByte();
            b0 = (bits & 1) != 0;
            b1 = (bits & 2) != 0;
            b2 = (bits & 4) != 0;
            b3 = (bits & 8) != 0;
            b4 = (bits & 16) != 0;
            b5 = (bits & 32) != 0;
            b6 = (bits & 64) != 0;
            b7 = (bits & 128) != 0;
        }

        // ── Byte-aligned primitives ────────────────────────

        public void Align()
        {
            if (_bitPos != 0) { _pos++; _bitPos = 0; }
        }

        public byte ReadByte()
        {
            Align();
            if (_pos >= _len) throw new InvalidOperationException("SyncReader: read past end");
            return _buf[_pos++];
        }

        public byte[] ReadBytes(int count)
        {
            Align();
            if (_pos + count > _len) throw new InvalidOperationException("SyncReader: read past end");
            byte[] result = new byte[count];
            Buffer.BlockCopy(_buf, _pos, result, 0, count);
            _pos += count;
            return result;
        }

        public void ReadBytes(byte[] dest, int offset, int count)
        {
            Align();
            if (_pos + count > _len) throw new InvalidOperationException("SyncReader: read past end");
            Buffer.BlockCopy(_buf, _pos, dest, offset, count);
            _pos += count;
        }

        // ── LEB128 VarInt ──────────────────────────────────

        public uint ReadVarInt()
        {
            Align();
            uint v = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _len) throw new InvalidOperationException("SyncReader: read past end");
                byte b = _buf[_pos++];
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 35) throw new FormatException("SyncReader: malformed VarInt");
            }
            return v;
        }

        public int ReadZigZag()
        {
            uint v = ReadVarInt();
            return (int)(v >> 1) ^ -(int)(v & 1);
        }

        // ── Floats ─────────────────────────────────────────

        public float ReadFloat32()
        {
            byte[] tmp = ReadBytes(4);
            return BitConverter.ToSingle(tmp, 0);
        }

        public float ReadFloat16()
        {
            Align();
            if (_pos + 2 > _len) throw new InvalidOperationException("SyncReader: read past end");
            ushort half = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8));
            _pos += 2;

            int sign = (half >> 15) & 1;
            int exp = (half >> 10) & 0x1F;
            int mant = half & 0x3FF;

            double v;
            if (exp == 0)
            {
                if (mant == 0) v = sign == 0 ? 0.0 : -0.0;
                else v = Math.Sign(sign) * mant * Math.Pow(2, -24);
            }
            else if (exp == 0x1F)
            {
                v = mant == 0
                    ? (sign == 0 ? double.PositiveInfinity : double.NegativeInfinity)
                    : double.NaN;
            }
            else
            {
                v = Math.Sign(sign) * (mant + 1024.0) * Math.Pow(2, exp - 25);
            }

            return (float)v;
        }

        /// <summary>Remaining readable bytes (byte-aligned).</summary>
        public int Remaining
        {
            get { Align(); return _len - _pos; }
        }
    }
}
