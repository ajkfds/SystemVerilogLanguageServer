using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemVerilogLanguageServer;

/// <summary>
/// Writes LSP-framed JSON messages to <see cref="System.IO.Stream"/> using
/// the standard <c>Content-Length</c> header.
/// </summary>
public sealed class LspStreamWriter
{
    private readonly Stream _stream;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public LspStreamWriter(Stream stream)
    {
        _stream = stream;
    }

    public async Task WriteMessageAsync(LspMessage message, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(message, s_jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        string header = $"Content-Length: {bytes.Length}\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        await _stream.WriteAsync(headerBytes, cancellationToken);
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }
}
