using System;

namespace CSM.Sync
{
    /// <summary>
    ///     Compact binary writer with VarInt and ZigZag encoding.
    ///     Backed by a growable byte array, reusable via Reset().
    /// </summary>
    public sealed class SyncWriter
    {
        private byte[] _buf;
        private int _len;

        public SyncWriter(int capacity = 256)
        {
            _buf = new byte[capacity];
            _len = 0;
        }

        public void WriteByte(byte v)
        {
            EnsureCapacity(1);
            _buf[_len++] = v;
        }

        public void WriteBytes(byte[] src, int offset, int count)
        {
            EnsureCapacity(count);
            Buffer.BlockCopy(src, offset, _buf, _len, count);
            _len += count;
        }

        /// <summary>LEB128 unsigned VarInt. 1 byte for 0-127, 2 for 128-16383.</summary>
        public void WriteVarInt(uint v)
        {
            while (v >= 0x80)
            {
                EnsureCapacity(1);
                _buf[_len++] = (byte)(v | 0x80);
                v >>= 7;
            }
            EnsureCapacity(1);
            _buf[_len++] = (byte)v;
        }

        /// <summary>ZigZag + VarInt for signed integers. 0→0, -1→1, 1→2, -2→3.</summary>
        public void WriteZigZag(int v)
        {
            WriteVarInt((uint)((v << 1) ^ (v >> 31)));
        }

        public byte[] ToArray()
        {
            byte[] result = new byte[_len];
            Buffer.BlockCopy(_buf, 0, result, 0, _len);
            return result;
        }

        public int Length { get { return _len; } }

        /// <summary>Reset for reuse (keeps allocated buffer).</summary>
        public void Reset()
        {
            _len = 0;
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
    ///     Compact binary reader with VarInt and ZigZag decoding.
    /// </summary>
    public sealed class SyncReader
    {
        private byte[] _buf;
        private int _pos;
        private int _end;

        public SyncReader(byte[] data) : this(data, 0, data.Length) { }
        public SyncReader(byte[] data, int offset, int count)
        {
            _buf = data;
            _pos = offset;
            _end = offset + count;
        }

        public byte ReadByte()
        {
            if (_pos >= _end) throw new InvalidOperationException("SyncReader: read past end");
            return _buf[_pos++];
        }

        public byte[] ReadBytes(int count)
        {
            if (_pos + count > _end) throw new InvalidOperationException("SyncReader: read past end");
            byte[] result = new byte[count];
            Buffer.BlockCopy(_buf, _pos, result, 0, count);
            _pos += count;
            return result;
        }

        public uint ReadVarInt()
        {
            uint v = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _end) throw new InvalidOperationException("SyncReader: read past end");
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

        public int Remaining { get { return _end - _pos; } }
    }
}
