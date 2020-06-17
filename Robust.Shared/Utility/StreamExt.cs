using System;
using System.IO;

namespace Robust.Shared.Utility
{
    /// <summary>
    ///     Extension methods for working with streams.
    /// </summary>
    public static class StreamExt
    {
        /// <summary>
        ///     Copies any stream into a byte array.
        /// </summary>
        /// <param name="stream">The stream to copy.</param>
        /// <returns>The byte array.</returns>
        public static byte[] CopyToArray(this Stream stream)
        {
            using (var memStream = new MemoryStream())
            {
                stream.CopyTo(memStream);
                return memStream.ToArray();
            }
        }

        /// <exception cref="EndOfStreamException">
        /// Thrown if not exactly <paramref name="amount"/> bytes could be read.
        /// </exception>
        public static void ReadExact(this Stream stream, Span<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var seg = buffer.Slice(read);
                var cRead = stream.Read(seg);
                if (cRead == 0)
                {
                    throw new EndOfStreamException();
                }

                read += cRead;
            }
        }
    }
}
