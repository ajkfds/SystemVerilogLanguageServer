using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SystemVerilogCore;
using SystemVerilogLanguageServer;
using SystemVerilogLanguageServer.Server;

namespace SystemVerilogLanguageServer;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            await RunSelfTestAsync();
            return;
        }

        // The LSP server speaks JSON-RPC over stdin/stdout. We avoid the
        // Console helper on purpose: the LSP client expects raw bytes, not
        // the .NET console encoding-aware wrappers, and writing to Console
        // can interleave with logging.
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();

        InMemorySystemVerilogCore core = new();
        LspHandler handler = new(core);
        LspStreamReader reader = new(input);
        LspStreamWriter writer = new(output);

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                string? json = await reader.ReadMessageAsync(cts.Token);
                if (json == null)
                {
                    break;
                }

                LspMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<LspMessage>(json);
                }
                catch (JsonException ex)
                {
                    await WriteErrorAsync(writer, null, -32700, $"Parse error: {ex.Message}", cts.Token);
                    continue;
                }
                if (message == null || message.Method == null) continue;

                bool isRequest = message.Id != null;
                try
                {
                    JsonElement parameters = message.Params is JsonElement p
                        ? p
                        : default;

                    if (isRequest)
                    {
                        object? result = handler.TryHandleRequest(message.Method, parameters, cts.Token, out var r)
                            ? r
                            : throw new InvalidOperationException($"Method not found: {message.Method}");
                        await writer.WriteMessageAsync(new LspMessage
                        {
                            Id = message.Id,
                            Result = result,
                        }, cts.Token);
                    }
                    else
                    {
                        if (!handler.TryHandleNotification(message.Method, parameters, cts.Token))
                        {
                            // Unknown notification: ignore (LSP clients treat
                            // this as a no-op).
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (isRequest)
                    {
                        await WriteErrorAsync(writer, message.Id, -32603, $"Internal error: {ex.Message}", cts.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    /// <summary>
    /// Drives the LSP handler with a tiny synthetic session: <c>initialize</c>
    /// followed by a <c>textDocument/didOpen</c>, then a
    /// <c>textDocument/definition</c>. Writes the resulting JSON responses
    /// to <see cref="Console.Out"/> so it can be used as a smoke test.
    /// </summary>
    private static async Task RunSelfTestAsync()
    {
        InMemorySystemVerilogCore core = new();
        LspHandler handler = new(core);
        using MemoryStream output = new();
        await using StreamWriter writer = new(output, leaveOpen: true);

        // Wrap our hand-rolled writer around the memory stream.
        LspStreamWriter lspWriter = new(output);

        // 1) initialize
        {
            const string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{}}}";
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement parameters = root.GetProperty("params");
            object? result = handler.TryHandleRequest("initialize", parameters, CancellationToken.None, out var r) ? r : null;
            await lspWriter.WriteMessageAsync(new LspMessage { Id = 1, Result = result }, CancellationToken.None);
        }

        // 2) didOpen
        {
            const string json = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/foo.sv\",\"languageId\":\"systemverilog\",\"text\":\"module foo; endmodule\"}}}";
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement parameters = doc.RootElement.GetProperty("params");
            handler.TryHandleNotification("textDocument/didOpen", parameters, CancellationToken.None);
        }

        // 3) definition
        {
            const string json = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/foo.sv\"},\"position\":{\"line\":0,\"character\":7}}}";
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement parameters = doc.RootElement.GetProperty("params");
            object? result = handler.TryHandleRequest("textDocument/definition", parameters, CancellationToken.None, out var r) ? r : null;
            await lspWriter.WriteMessageAsync(new LspMessage { Id = 2, Result = result }, CancellationToken.None);
        }

        // 4) documentSymbol
        {
            const string json = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/documentSymbol\",\"params\":{\"textDocument\":{\"uri\":\"file:///tmp/foo.sv\"}}}";
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement parameters = doc.RootElement.GetProperty("params");
            object? result = handler.TryHandleRequest("textDocument/documentSymbol", parameters, CancellationToken.None, out var r) ? r : null;
            await lspWriter.WriteMessageAsync(new LspMessage { Id = 3, Result = result }, CancellationToken.None);
        }

        output.Position = 0;
        using StreamReader reader = new(output);
        string all = await reader.ReadToEndAsync();
        await Console.Out.WriteAsync(all);
    }

    private static async Task WriteErrorAsync(LspStreamWriter writer, object? id, int code, string message, CancellationToken cancellationToken)
    {
        await writer.WriteMessageAsync(new LspMessage
        {
            Id = id,
            Error = new LspError
            {
                Code = code,
                Message = message,
            },
        }, cancellationToken);
    }
}
