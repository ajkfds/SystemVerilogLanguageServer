using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

        // Debugging transport: `--port <n>` makes the server listen on the
        // loopback interface instead of using stdin/stdout. The extension then
        // connects to that port (see SVLSP_SERVER_PORT in extension.js), which
        // lets the C# debugger launch this process directly while the
        // extension host runs in parallel (F5 in RtlEditor2.code-workspace).
        int port = ParsePortArgument(args);
        if (port > 0)
        {
            await RunTcpServerAsync(port);
            return;
        }

        // The LSP server speaks JSON-RPC over stdin/stdout. We avoid the
        // Console helper on purpose: the LSP client expects raw bytes, not
        // the .NET console encoding-aware wrappers, and writing to Console
        // can interleave with logging.
        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await RunLoopAsync(input, output, cts.Token);
    }

    /// <summary>
    /// Reads <c>--port &lt;n&gt;</c> / <c>--port=&lt;n&gt;</c> from the command line.
    /// Returns 0 when the server should speak LSP over stdin/stdout (the
    /// default, used by the packaged extension).
    /// </summary>
    private static int ParsePortArgument(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--port" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int separated) && separated > 0)
            {
                return separated;
            }
            if (arg.StartsWith("--port=", StringComparison.Ordinal) &&
                int.TryParse(arg.Substring("--port=".Length), out int inline) && inline > 0)
            {
                return inline;
            }
        }
        return 0;
    }

    /// <summary>
    /// Listens on the loopback interface and serves a single LSP session over
    /// TCP. Used for debugging: the extension connects to the port instead of
    /// spawning the server, so the C# debugger can launch this process
    /// directly (F5 in RtlEditor2.code-workspace starts both the extension
    /// host and this server).
    /// </summary>
    private static async Task RunTcpServerAsync(int port)
    {
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        Console.Error.WriteLine($"listening for an LSP client on 127.0.0.1:{port}");

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            // Accept repeatedly: "Developer: Reload Window" restarts the
            // extension host, which reconnects to this same server. Each
            // connection gets a fresh LSP session (the client re-initializes).
            while (!cts.IsCancellationRequested)
            {
                using TcpClient tcp = await listener.AcceptTcpClientAsync(cts.Token);
                tcp.NoDelay = true;
                Console.Error.WriteLine("LSP client connected");
                using NetworkStream stream = tcp.GetStream();
                await RunLoopAsync(stream, stream, cts.Token);
                Console.Error.WriteLine("LSP client disconnected");
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The LSP message loop. Shared by the stdio and TCP transports.
    /// </summary>
    private static async Task RunLoopAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        InMemorySystemVerilogCore core = new();
        LspHandler handler = new(core);
        LspStreamReader reader = new(input);
        LspStreamWriter writer = new(output);

        // Server-initiated notifications (publishDiagnostics) are pushed
        // synchronously while handling client messages, so the writer is
        // only ever used from this single loop: no interleaving.
        handler.NotificationSender = (msg) => writer.WriteMessageAsync(msg, cancellationToken).GetAwaiter().GetResult();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? json = await reader.ReadMessageAsync(cancellationToken);
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
                    await WriteErrorAsync(writer, null, -32700, $"Parse error: {ex.Message}", cancellationToken);
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
                        object? result = handler.TryHandleRequest(message.Method, parameters, cancellationToken, out var r)
                            ? r
                            : throw new InvalidOperationException($"Method not found: {message.Method}");
                        await writer.WriteMessageAsync(new LspMessage
                        {
                            Id = message.Id,
                            Result = result,
                        }, cancellationToken);
                    }
                    else
                    {
                        if (!handler.TryHandleNotification(message.Method, parameters, cancellationToken))
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
                    // Log the full stack: the client only sees the message, and a
                    // silent internal error (e.g. in initialize) leaves the whole
                    // extension without a session (the tree view stays empty).
                    Console.Error.WriteLine($"error handling {message.Method}: {ex}");
                    if (isRequest)
                    {
                        await WriteErrorAsync(writer, message.Id, -32603, $"Internal error: {ex.Message}", cancellationToken);
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
