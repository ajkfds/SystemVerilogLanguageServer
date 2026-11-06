using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using SystemVerilogCore.Documents;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// <c>rtl/snippet</c> — the text the editor's snippet would insert, so the VS
/// Code extension can apply it with <c>TextEditor.insertSnippet</c>.
///
/// <para>
/// This is a non-standard request (vendor prefix <c>rtl/</c>). The decision of
/// <b>what</b> to insert (port enumeration, parameter overrides, connection
/// generation, port inversion, modport derivation) stays in the plugin's
/// UI-free <c>CoreBridge.SnippetAdapter</c>; the client only performs the
/// mechanical insertion. That keeps the snippet logic in one place (C#) and
/// the extension a pure pipe, per the architecture note in
/// <c>memory/vscode_extension_architecture.md</c>.
/// </para>
///
/// <para>
/// Params: <c>{ "textDocument": { "uri": ... }, "position": { line, character },
/// "kind": "moduleInstance", "argument": "my_module",
/// "selection": { "start": { line, character }, "end": { line, character } } }</c>.
/// <c>argument</c> and <c>selection</c> are optional.
/// </para>
/// </summary>
public sealed partial class LspHandler
{
    /// <summary>
    /// Returns the snippet text for the requested kind at the caret, or the
    /// candidate list when the request needs a further choice.
    /// </summary>
    private object? HandleSnippet(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return new SnippetResultDto();
        if (!td.TryGetProperty("uri", out var uriProp)) return new SnippetResultDto();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        InMemoryFile? file = TryLoadFileFromDisk(project, uri);
        if (file == null) return new SnippetResultDto { Message = "the file is not known to the server" };

        string kind = parameters.TryGetProperty("kind", out var kindProp)
            && kindProp.ValueKind == JsonValueKind.String
            ? kindProp.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrEmpty(kind)) return new SnippetResultDto { Message = "no snippet kind given" };

        string? argument = parameters.TryGetProperty("argument", out var argProp)
            && argProp.ValueKind == JsonValueKind.String
            ? argProp.GetString()
            : null;

        int index = ResolveIndex(file, parameters, "position");
        int selectionStart = ResolveIndex(file, parameters, "selection", "start");
        int selectionEnd = ResolveIndex(file, parameters, "selection", "end");

        pluginVerilog.CoreBridge.SnippetAdapter.SnippetResult? result =
            file.GetSnippets(index, kind, argument, selectionStart, selectionEnd);
        if (result == null) return new SnippetResultDto { Message = "the file has not been parsed yet" };

        SnippetResultDto dto = new SnippetResultDto { Message = result.Message };
        dto.Candidates.AddRange(result.Candidates);
        foreach (pluginVerilog.CoreBridge.SnippetAdapter.SnippetEntry entry in result.Snippets)
        {
            dto.Snippets.Add(new SnippetDto
            {
                Label = entry.Label,
                Detail = entry.Detail,
                Kind = entry.Kind,
                Start = entry.Start,
                End = entry.End,
                SnippetText = entry.SnippetText,
            });
        }
        return dto;
    }

    /// <summary>
    /// Reads a line/character position out of <paramref name="parameters"/> and
    /// converts it to a document offset. Returns 0 when the position is absent.
    /// </summary>
    private static int ResolveIndex(
        InMemoryFile file,
        JsonElement parameters,
        string property,
        string? nested = null)
    {
        if (!parameters.TryGetProperty(property, out var element)) return 0;
        if (nested != null)
        {
            if (!element.TryGetProperty(nested, out var inner)) return 0;
            element = inner;
        }

        int line = element.TryGetProperty("line", out var lineProp) ? lineProp.GetInt32() : 0;
        int character = element.TryGetProperty("character", out var charProp) ? charProp.GetInt32() : 0;

        ISystemVerilogCodeDocument doc = file.CodeDocument;
        if (line < 0) line = 0;
        if (line >= doc.LineCount) line = doc.LineCount - 1;
        if (line < 0) return 0;

        int lineStart = doc.GetLineStartIndex(line);
        int lineLength = doc.GetLineLength(line);
        if (character < 0) character = 0;
        if (character > lineLength) character = lineLength;
        return lineStart + character;
    }
}
