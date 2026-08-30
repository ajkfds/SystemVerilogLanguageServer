using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SystemVerilogCore;
using SystemVerilogCore.Documents;
using SystemVerilogCore.Diagnostics;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// Dispatcher from LSP <c>method</c> names to handlers. Handlers are
/// synchronous for now; the project-level adapters used by the language
/// server cache everything in memory and the round-trip to the parser
/// (if any) is the responsibility of the adapter, not the dispatcher.
/// </summary>
public sealed class LspHandler
{
    private readonly InMemorySystemVerilogCore _core;
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, object?>> _requestHandlers = new();
    private readonly Dictionary<string, Action<JsonElement, CancellationToken>> _notificationHandlers = new();

    public LspHandler(InMemorySystemVerilogCore core)
    {
        _core = core;
        RegisterDefaultHandlers();
    }

    private void RegisterDefaultHandlers()
    {
        _requestHandlers["initialize"] = HandleInitialize;
        _requestHandlers["shutdown"] = HandleShutdown;
        _requestHandlers["textDocument/definition"] = HandleDefinition;
        _requestHandlers["textDocument/references"] = HandleReferences;
        _requestHandlers["textDocument/documentSymbol"] = HandleDocumentSymbol;
        _requestHandlers["textDocument/hover"] = HandleHover;

        _notificationHandlers["initialized"] = HandleInitialized;
        _notificationHandlers["exit"] = HandleExit;
        _notificationHandlers["textDocument/didOpen"] = HandleDidOpen;
        _notificationHandlers["textDocument/didChange"] = HandleDidChange;
        _notificationHandlers["textDocument/didClose"] = HandleDidClose;
    }

    public bool TryHandleRequest(string method, JsonElement parameters, CancellationToken cancellationToken, out object? result)
    {
        if (_requestHandlers.TryGetValue(method, out var handler))
        {
            result = handler(parameters, cancellationToken);
            return true;
        }
        result = null;
        return false;
    }

    public bool TryHandleNotification(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (_notificationHandlers.TryGetValue(method, out var handler))
        {
            handler(parameters, cancellationToken);
            return true;
        }
        return false;
    }

    // ---------------------- initialize / lifecycle ----------------------

    private object? HandleInitialize(JsonElement parameters, CancellationToken cancellationToken)
    {
        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full,
                },
                HoverProvider = true,
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
            },
            ServerInfo = new ServerInfo { Name = "SystemVerilogLanguageServer", Version = "0.1.0" },
        };
    }

    private object? HandleShutdown(JsonElement parameters, CancellationToken cancellationToken) => null;

    private void HandleInitialized(JsonElement parameters, CancellationToken cancellationToken)
    {
    }

    private void HandleExit(JsonElement parameters, CancellationToken cancellationToken)
    {
        Environment.Exit(0);
    }

    // ---------------------- textDocument/* ----------------------

    private void HandleDidOpen(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var textDocumentProp)) return;
        if (!textDocumentProp.TryGetProperty("uri", out var uriProp)) return;
        if (!textDocumentProp.TryGetProperty("text", out var textProp)) return;

        string uri = uriProp.GetString() ?? string.Empty;
        string text = textProp.GetString() ?? string.Empty;
        string languageId = textDocumentProp.TryGetProperty("languageId", out var lang) ? lang.GetString() ?? "systemverilog" : "systemverilog";
        bool isSystemVerilog = languageId.IndexOf("systemverilog", StringComparison.OrdinalIgnoreCase) >= 0;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        project.AddOrUpdateFile(uri, ToAbsolutePath(uri), text, isSystemVerilog);
    }

    private void HandleDidChange(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        if (!parameters.TryGetProperty("contentChanges", out var changes)) return;

        string uri = uriProp.GetString() ?? string.Empty;
        if (changes.ValueKind != JsonValueKind.Array) return;
        if (changes.GetArrayLength() == 0) return;

        var first = changes[0];
        if (!first.TryGetProperty("text", out var textProp)) return;
        string text = textProp.GetString() ?? string.Empty;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        ISystemVerilogFile? existing = project.FindFile(uri);
        bool isSystemVerilog = existing?.IsSystemVerilog ?? true;
        project.AddOrUpdateFile(uri, ToAbsolutePath(uri), text, isSystemVerilog);
    }

    private void HandleDidClose(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        string uri = uriProp.GetString() ?? string.Empty;
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        project.RemoveFile(uri);
    }

    private object? HandleDefinition(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return Array.Empty<Location>();
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        ISystemVerilogNamedElement? def = project.FindDefinition(file, index);
        if (def?.DefinitionRange is { } r)
        {
            return new[] { ToLocation(file, r) };
        }
        return Array.Empty<Location>();
    }

    private object? HandleReferences(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return Array.Empty<Location>();
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        IReadOnlyList<ISystemVerilogNamedElement> refs = project.FindReferences(file, index);
        List<Location> locations = new();
        foreach (ISystemVerilogNamedElement r in refs)
        {
            if (r.DefinitionRange is { } range && r.File is { } f)
            {
                locations.Add(ToLocation(f, range));
            }
        }
        return locations;
    }

    private object? HandleDocumentSymbol(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<DocumentSymbol>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<DocumentSymbol>();
        string uri = uriProp.GetString() ?? string.Empty;
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<DocumentSymbol>();
        ISystemVerilogDocument? doc = project.GetDocument(file);
        if (doc == null) return Array.Empty<DocumentSymbol>();

        return new[]
        {
            new DocumentSymbol
            {
                Name = doc.Root.Name,
                Kind = SymbolKind.Namespace,
                Range = ToLspRange(file, new SystemVerilogRange(0, file.CodeDocument.Length)),
                SelectionRange = ToLspRange(file, new SystemVerilogRange(0, 0)),
            },
        };
    }

    private object? HandleHover(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return null;
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        ISystemVerilogNamedElement? def = project.FindDefinition(file, index);
        if (def == null) return null;
        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = "plaintext",
                Value = $"{def.Kind} {def.Name}",
            },
            Range = def.DefinitionRange is { } r ? ToLspRange(file, r) : null,
        };
    }

    // ---------------------- helpers ----------------------

    private bool TryGetFileAndIndex(JsonElement parameters, out ISystemVerilogFile file, out int index)
    {
        file = null!;
        index = 0;
        if (!parameters.TryGetProperty("textDocument", out var td)) return false;
        if (!td.TryGetProperty("uri", out var uriProp)) return false;
        string uri = uriProp.GetString() ?? string.Empty;
        if (!parameters.TryGetProperty("position", out var pos)) return false;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        ISystemVerilogFile? found = project.FindFile(uri);
        if (found == null) return false;

        int line = pos.TryGetProperty("line", out var lineProp) ? lineProp.GetInt32() : 0;
        int character = pos.TryGetProperty("character", out var charProp) ? charProp.GetInt32() : 0;
        int lineStart = found.CodeDocument.GetLineStartIndex(line);
        index = lineStart + character;
        if (index > found.CodeDocument.Length) index = found.CodeDocument.Length;
        file = found;
        return true;
    }

    private InMemoryProject GetOrCreateProjectForUri(string uri) => _core.GetOrCreateProject("default");

    private static string ToAbsolutePath(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string local = uri.Substring("file://".Length);
            return Uri.UnescapeDataString(local);
        }
        return uri;
    }

    private static Location ToLocation(ISystemVerilogFile file, SystemVerilogRange range)
    {
        return new Location
        {
            Uri = file.Id,
            Range = ToLspRange(file, range),
        };
    }

    private static LspRange ToLspRange(ISystemVerilogFile file, SystemVerilogRange range)
    {
        ISystemVerilogCodeDocument doc = file.CodeDocument;
        int startLine = doc.GetLineAt(range.StartIndex);
        int startCol = range.StartIndex - doc.GetLineStartIndex(startLine);
        int endIndex = range.EndIndex;
        if (endIndex > doc.Length) endIndex = doc.Length;
        int endLine = doc.GetLineAt(endIndex);
        int endCol = endIndex - doc.GetLineStartIndex(endLine);
        return new LspRange
        {
            Start = new LspPosition { Line = startLine, Character = startCol },
            End = new LspPosition { Line = endLine, Character = endCol },
        };
    }
}

// ---------------------- LSP DTOs (subset) ----------------------

public sealed class InitializeResult
{
    [JsonPropertyName("capabilities")] public ServerCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")] public ServerInfo ServerInfo { get; set; } = new();
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")] public TextDocumentSyncOptions? TextDocumentSync { get; set; }
    [JsonPropertyName("hoverProvider")] public bool HoverProvider { get; set; }
    [JsonPropertyName("definitionProvider")] public bool DefinitionProvider { get; set; }
    [JsonPropertyName("referencesProvider")] public bool ReferencesProvider { get; set; }
    [JsonPropertyName("documentSymbolProvider")] public bool DocumentSymbolProvider { get; set; }
}

public sealed class TextDocumentSyncOptions
{
    [JsonPropertyName("openClose")] public bool OpenClose { get; set; }
    [JsonPropertyName("change")] public TextDocumentSyncKind Change { get; set; }
}

public enum TextDocumentSyncKind { None = 0, Full = 1, Incremental = 2 }

public sealed class ServerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class Location
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
}

public sealed class LspRange
{
    [JsonPropertyName("start")] public LspPosition Start { get; set; } = new();
    [JsonPropertyName("end")] public LspPosition End { get; set; } = new();
}

public sealed class LspPosition
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("character")] public int Character { get; set; }
}

public sealed class DocumentSymbol
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public SymbolKind Kind { get; set; }
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("selectionRange")] public LspRange SelectionRange { get; set; } = new();
}

public enum SymbolKind
{
    File = 1, Module = 2, Namespace = 3, Package = 4, Class = 5, Method = 6,
    Property = 7, Field = 8, Constructor = 9, Enum = 10, Interface = 11,
    Function = 12, Variable = 13, Constant = 14, String = 15, Number = 16,
}

public sealed class Hover
{
    [JsonPropertyName("contents")] public MarkupContent Contents { get; set; } = new();
    [JsonPropertyName("range")] public LspRange? Range { get; set; }
}

public sealed class MarkupContent
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "plaintext";
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
}
