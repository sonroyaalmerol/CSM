using System.IO;
using CSM.API.Commands;
using CSM.Commands;

namespace CSM.Util
{
    public static class Serializer
    {
        // Reusable MemoryStream to avoid allocating a new one per serialize call.
        // This reduces GC pressure, especially under high-latency command batching.
        private static readonly MemoryStream _stream = new MemoryStream(4096);
        private static readonly object _serializeLock = new object();

        /// <summary>
        ///     Serializes the command into a byte array for sending over the network.
        ///     Uses a pooled MemoryStream to reduce GC allocations.
        /// </summary>
        /// <returns>A byte array containing the message.</returns>
        public static byte[] Serialize(CommandBase cmd)
        {
            lock (_serializeLock)
            {
                _stream.SetLength(0);
                _stream.Position = 0;

                CommandInternal.Instance.Model.Serialize(_stream, cmd);

                // Copy the written portion — ToArray() returns the full underlying buffer,
                // so we use GetBuffer() + length instead.
                int length = (int)_stream.Position;
                byte[] result = new byte[length];
                System.Buffer.BlockCopy(_stream.GetBuffer(), 0, result, 0, length);
                return result;
            }
        }
    }
}
