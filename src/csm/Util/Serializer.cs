using System.IO;
using CSM.API.Commands;
using CSM.Commands;

namespace CSM.Util
{
    public static class Serializer
    {
        /// <summary>
        ///     Thread-local pooled MemoryStream to avoid allocating a new one per
        ///     serialize call while remaining safe for multi-threaded use.
        ///     Cities: Skylines is single-threaded for simulation but the
        ///     thread-local guards against any unexpected cross-thread calls.
        /// </summary>
        [System.ThreadStatic]
        private static MemoryStream _tlsStream;

        private static MemoryStream GetStream()
        {
            if (_tlsStream == null)
                _tlsStream = new MemoryStream(4096);
            return _tlsStream;
        }

        /// <summary>
        ///     Serializes the command into a byte array for sending over the network.
        ///     Uses a thread-local pooled MemoryStream to reduce GC allocations
        ///     without lock contention.
        /// </summary>
        /// <returns>A byte array containing the message.</returns>
        public static byte[] Serialize(CommandBase cmd)
        {
            MemoryStream stream = GetStream();
            stream.SetLength(0);
            stream.Position = 0;

            CommandInternal.Instance.Model.Serialize(stream, cmd);

            // Copy the written portion — ToArray() returns the full underlying buffer,
            // so we use GetBuffer() + length instead.
            int length = (int)stream.Position;
            byte[] result = new byte[length];
            System.Buffer.BlockCopy(stream.GetBuffer(), 0, result, 0, length);
            return result;
        }
    }
}
