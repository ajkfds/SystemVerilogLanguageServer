# SystemVerilogLanguageServer

A minimal [Language Server Protocol](https://microsoft.github.io/language-server-protocol/)
implementation for SystemVerilog. It speaks LSP over JSON-RPC on
`stdin`/`stdout`.

## Status

First cut. The following capabilities are advertised and respond to
requests:

* `initialize` / `initialized` / `shutdown` / `exit`
* `textDocument/didOpen` / `didChange` / `didClose`
* `textDocument/definition`
* `textDocument/references`
* `textDocument/documentSymbol`
* `textDocument/hover`

Symbol lookups (`definition`, `references`, `documentSymbol`, `hover`)
currently run against an in-memory model that only retains the text the
client has sent us. Wiring them to the real SystemVerilog parser
happens through the `ISystemVerilogCore` seam defined in
`SystemVerilogCore`; see the `README.md` in that project for the
adapter story.

## Building

```
dotnet build SystemVerilogCore/SystemVerilogCore/SystemVerilogCore.csproj
dotnet build SystemVerilogLanguageServer/SystemVerilogLanguageServer/SystemVerilogLanguageServer.csproj
```

## Architecture

```
        client (editor)                  server process
        ──────────────                   ──────────────
   LSP request/notification  ──stdin/stdout──▶  LspStreamReader
                                                   │
                                                   ▼
                                            LspHandler (dispatch)
                                                   │
                                                   ▼
                                  InMemorySystemVerilogCore  (default)
                                                   │   ▲
                                                   │   │ adapter
                                                   ▼   │
                                  ISystemVerilogCore / ISystemVerilogProject
                                       (defined in SystemVerilogCore)
```

`SystemVerilogCore` only owns the interfaces. The language server
default implementation is `InMemorySystemVerilogCore`; the plugin will
later provide a parser-backed implementation that satisfies the same
interfaces.
