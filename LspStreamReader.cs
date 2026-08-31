using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SystemVerilogLanguageServer;

/// <summary>
/// Reads LSP-framed JSON messages from <see cref="System.IO.Stream"/> using
/// the standard <c>Content-Length</c> header. One instance is shared between
/// the read and write loops.
/// </summary>
public sealed class LspStreamReader
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[1024];

    public LspStreamReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        int contentLength = -1;
        string? contentType = null;

        // Read headers line by line until the empty line.
        while (true)
        {
            int read = await ReadLineAsync(_headerBuffer, cancellationToken);
            if (read < 0)
            {
                return null; // EOF
            }

            string line = Encoding.ASCII.GetString(_headerBuffer, 0, read);
            if (line.Length == 0)
            {
                break;
            }

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string name = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).Trim();

            if (string.Equals(name, "Content-Length", System.StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out contentLength))
                {
                    return null;
                }
            }
            else if (string.Equals(name, "Content-Type", System.StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
        }

        if (contentLength < 0)
        {
            return null;
        }

        byte[] body = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            int read = 0;
            while (read < contentLength)
            {
                int n = await _stream.ReadAsync(body.AsMemory(read, contentLength - read), cancellationToken);
                if (n == 0)
                {
                    return null;
                }
                read += n;
            }
            return Encoding.UTF8.GetString(body, 0, contentLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    private async Task<int> ReadLineAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int index = 0;
        while (true)
        {
            if (index >= buffer.Length)
            {
                throw new System.IO.IOException("LSP header line too long");
            }
            int b = _stream.ReadByte();
            if (b < 0)
            {
                return index == 0 ? -1 : index;
            }
            if (b == '\n')
            {
                // Strip optional trailing '\r'.
                if (index > 0 && buffer[index - 1] == '\r')
                {
                    index--;
                }
                return index;
            }
            buffer[index++] = (byte)b;
        }
    }
}
