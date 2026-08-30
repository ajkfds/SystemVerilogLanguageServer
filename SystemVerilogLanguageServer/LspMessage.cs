using System.Text.Json.Serialization;

namespace SystemVerilogLanguageServer;

/// <summary>
/// Minimal subset of LSP and JSON-RPC envelope types. Hand-written instead
/// of taking a dependency on a full LSP library so that the core wiring
/// (read/write framed messages over stdin/stdout) is transparent and easy
/// to extend.
/// </summary>
public sealed class LspMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public LspError? Error { get; set; }
}

public sealed class LspError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}
