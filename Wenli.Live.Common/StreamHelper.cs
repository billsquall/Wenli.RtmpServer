﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Wenli.Live.Common
{
    public static class StreamHelper
    {
        public static byte[] ReadBytes(Stream stream, int count)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var result = new byte[count];
            var bytesRead = 0;
            while (count > 0)
            {
                var n = stream.Read(result, bytesRead, count);
                if (n == 0)
                    break;
                bytesRead += n;
                count -= n;
            }

            if (bytesRead != result.Length)
                throw new EndOfStreamException();

            return result;
        }

        public static async Task<byte[]> ReadBytesAsync(Stream stream, int count, CancellationToken cancellationToken)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var result = new byte[count];
            var bytesRead = 0;
            while (count > 0)
            {
                var n = await stream.ReadAsync(result, bytesRead, count, cancellationToken);
                if (n == 0)
                    break;
                bytesRead += n;
                count -= n;
            }

            if (bytesRead != result.Length)
                throw new EndOfStreamException();

            return result;
        }

        public static async Task<byte[]> ReadBytesAsync(this Stream stream, int count)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var result = new byte[count];
            var bytesRead = 0;
            while (count > 0)
            {
                var n = await stream.ReadAsync(result, bytesRead, count).ConfigureAwait(false);
                if (n == 0)
                    break;
                bytesRead += n;
                count -= n;
            }

            if (bytesRead != result.Length)
                throw new EndOfStreamException();

            return result;
        }

        public static async Task<byte> ReadByteAsync(this Stream stream)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, 0, 1);
            if (read == 0)
                throw new EndOfStreamException();
            return buffer[0];
        }
    }
}
