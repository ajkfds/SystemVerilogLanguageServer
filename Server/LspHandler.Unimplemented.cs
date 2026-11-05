using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using SystemVerilogCore;
using SystemVerilogCore.Documents;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// Standard LSP features that are <b>wired but not implemented yet</b>.
///
/// <para>
/// The dispatcher (<see cref="LspHandler"/>) registers these methods and the
/// server advertises the matching capabilities, so the client registers its
/// providers and the round-trip works end to end. Each handler currently
/// returns an empty result; the real implementation is filled in here later
/// without touching the dispatcher or the capability advertisement.
/// </para>
///
/// <para>
/// Keeping them in their own partial file keeps <c>LspHandler.cs</c> readable:
/// the implemented features stay in the main file, the stubs live here.
/// </para>
/// </summary>
public sealed partial class LspHandler
{
    // ---------------------- text editing ----------------------

    /// <summary>
    /// textDocument/formatting — reformats every module instantiation in the
    /// document (the same scope as the editor's "autoFormat" tool). Empty when
    /// there is nothing to format.
    /// </summary>
    private object? HandleFormatting(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<TextEdit>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<TextEdit>();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<TextEdit>();

        List<TextEdit> edits = new();
        foreach (var e in file.GetFormattingEdits())
        {
            edits.Add(new TextEdit
            {
                Range = ToLspRange(file, new SystemVerilogRange(e.Start, e.End - e.Start)),
                NewText = e.NewText,
            });
        }
        return edits;
    }

    /// <summary>
    /// textDocument/rangeFormatting — the same edits as
    /// <c>textDocument/formatting</c>, filtered to the instantiations that lie
    /// inside the requested range.
    /// </summary>
    private object? HandleRangeFormatting(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<TextEdit>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<TextEdit>();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<TextEdit>();

        int rangeStart = 0;
        int rangeEnd = int.MaxValue;
        if (parameters.TryGetProperty("range", out var range)
            && range.TryGetProperty("start", out var rs)
            && range.TryGetProperty("end", out var re))
        {
            int startLine = rs.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
            int startChar = rs.TryGetProperty("character", out var sc) ? sc.GetInt32() : 0;
            int endLine = re.TryGetProperty("line", out var el) ? el.GetInt32() : 0;
            int endChar = re.TryGetProperty("character", out var ec) ? ec.GetInt32() : 0;
            ISystemVerilogCodeDocument doc = file.CodeDocument;
            rangeStart = doc.GetLineStartIndex(startLine) + startChar;
            rangeEnd = doc.GetLineStartIndex(endLine) + endChar;
        }

        List<TextEdit> edits = new();
        foreach (var e in file.GetFormattingEdits())
        {
            if (e.Start < rangeStart || e.End > rangeEnd) continue;
            edits.Add(new TextEdit
            {
                Range = ToLspRange(file, new SystemVerilogRange(e.Start, e.End - e.Start)),
                NewText = e.NewText,
            });
        }
        return edits;
    }

    /// <summary>textDocument/onTypeFormatting — returns no edits yet.</summary>
    private object? HandleOnTypeFormatting(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<TextEdit>();
    }

    /// <summary>
    /// textDocument/rename — renames every occurrence of the symbol under the
    /// caret (declaration + all references, project-wide) to the new name.
    ///
    /// <para>
    /// The reference set comes from the same lookup as
    /// <c>textDocument/references</c>, so the rename covers exactly what the
    /// editor would highlight. Returns null when the caret is not on a symbol.
    /// </para>
    /// </summary>
    private object? HandleRename(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return null;
        if (!parameters.TryGetProperty("newName", out var newNameProp)) return null;
        string newName = newNameProp.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(newName)) return null;

        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        IReadOnlyList<ISystemVerilogNamedElement> refs = project.FindReferences(file, index);
        if (refs.Count == 0) return null;

        WorkspaceEdit edit = new WorkspaceEdit { Changes = new Dictionary<string, List<TextEdit>>() };
        foreach (ISystemVerilogNamedElement r in refs)
        {
            if (r.DefinitionRange is not { } range || r.File is not { } f) continue;
            if (!edit.Changes.TryGetValue(f.Id, out List<TextEdit>? list))
            {
                list = new List<TextEdit>();
                edit.Changes[f.Id] = list;
            }
            list.Add(new TextEdit { Range = ToLspRange(f, range), NewText = newName });
        }
        return edit.Changes.Count == 0 ? null : edit;
    }

    /// <summary>
    /// textDocument/prepareRename — the range of the symbol under the caret and
    /// its current name, so the client can pre-fill the rename box. Null when
    /// the caret is not on a symbol.
    /// </summary>
    private object? HandlePrepareRename(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return null;
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        ISystemVerilogNamedElement? def = project.FindDefinition(file, index);
        if (def?.DefinitionRange is not { } range || def.File is not { } f) return null;

        return new PrepareRenameResult
        {
            Range = ToLspRange(f, range),
            Placeholder = def.Name,
        };
    }

    /// <summary>
    /// textDocument/codeAction — quick fixes for the diagnostics the client
    /// reports in the request context.
    ///
    /// <para>
    /// No fix is offered yet: the parser's diagnostics do not carry a machine
    /// actionable suggestion, so the handler returns an empty list. The wiring
    /// (request → handler → client) is in place, so adding a fix later only
    /// needs the mapping here.
    /// </para>
    /// </summary>
    private object? HandleCodeAction(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<CodeAction>();
    }

    /// <summary>textDocument/codeLens — returns no lenses yet.</summary>
    private object? HandleCodeLens(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<CodeLens>();
    }

    // ---------------------- input assistance ----------------------

    /// <summary>
    /// textDocument/signatureHelp — the argument hint the editor would pop up
    /// at the caret (the port / parameter label of the argument being typed),
    /// produced by the plugin's partial parse. Null when there is no hint.
    /// </summary>
    private object? HandleSignatureHelp(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return null;
        if (file is not InMemoryFile mem) return null;

        IReadOnlyList<pluginVerilog.CoreBridge.SignatureHelpAdapter.SignatureEntry>? signatures =
            mem.GetSignatures(index);
        if (signatures == null || signatures.Count == 0) return null;

        SignatureHelp help = new SignatureHelp();
        foreach (var s in signatures)
        {
            help.Signatures.Add(new SignatureInformation { Label = s.Label });
        }
        help.ActiveSignature = 0;
        help.ActiveParameter = 0;
        return help;
    }

    /// <summary>textDocument/completionItem/resolve — returns the item unchanged yet.</summary>
    private object? HandleCompletionResolve(JsonElement parameters, CancellationToken cancellationToken)
    {
        return null;
    }

    /// <summary>textDocument/inlayHint — returns no hints yet.</summary>
    private object? HandleInlayHint(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<InlayHint>();
    }

    // ---------------------- navigation ----------------------

    /// <summary>
    /// textDocument/declaration — the declaration of the symbol under the
    /// caret. SystemVerilog has no separate declaration/definition split, so
    /// this resolves exactly like <c>textDocument/definition</c>.
    /// </summary>
    private object? HandleDeclaration(JsonElement parameters, CancellationToken cancellationToken)
    {
        return HandleDefinition(parameters, cancellationToken);
    }

    /// <summary>
    /// textDocument/typeDefinition — the declaration of the symbol under the
    /// caret. The parser does not expose a separate "type" target yet, so this
    /// resolves like <c>textDocument/definition</c> (the declared object).
    /// </summary>
    private object? HandleTypeDefinition(JsonElement parameters, CancellationToken cancellationToken)
    {
        return HandleDefinition(parameters, cancellationToken);
    }

    /// <summary>
    /// textDocument/implementation — the declaration of the symbol under the
    /// caret. Interface-class / virtual-method implementation lookup is not
    /// modelled yet, so this resolves like <c>textDocument/definition</c>.
    /// </summary>
    private object? HandleImplementation(JsonElement parameters, CancellationToken cancellationToken)
    {
        return HandleDefinition(parameters, cancellationToken);
    }

    /// <summary>
    /// textDocument/documentHighlight — highlights every occurrence of the
    /// symbol under the caret inside the same document (declaration + uses).
    /// Uses the same reference lookup as <c>textDocument/references</c>, then
    /// keeps only the ranges that live in the requested file.
    /// </summary>
    private object? HandleDocumentHighlight(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return Array.Empty<DocumentHighlight>();
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        IReadOnlyList<ISystemVerilogNamedElement> refs = project.FindReferences(file, index);

        List<DocumentHighlight> highlights = new();
        foreach (ISystemVerilogNamedElement r in refs)
        {
            if (r.DefinitionRange is not { } range || r.File is not { } f) continue;
            if (!string.Equals(f.Id, file.Id, StringComparison.Ordinal)) continue;
            highlights.Add(new DocumentHighlight
            {
                Range = ToLspRange(f, range),
                // 1 = Text (a plain occurrence), 3 = Write (the declaration)
                Kind = (r is InMemoryElement ie && ie.IsReference) ? 1 : 3,
            });
        }
        return highlights;
    }

    /// <summary>
    /// textDocument/foldingRange — the fold regions the parser recorded on the
    /// document (begin/end, module/endmodule, ...), converted to LSP line
    /// ranges. Empty when the file has not been parsed yet.
    /// </summary>
    private object? HandleFoldingRange(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<FoldingRange>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<FoldingRange>();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<FoldingRange>();

        List<FoldingRange> ranges = new();
        foreach ((int startLine, int endLine, string? name) in file.GetFoldingRanges())
        {
            ranges.Add(new FoldingRange
            {
                StartLine = startLine,
                EndLine = endLine,
                Kind = name,
            });
        }
        return ranges;
    }

    /// <summary>
    /// textDocument/selectionRange — for each requested position, the chain of
    /// enclosing ranges (word → line → document). The parser does not expose
    /// the enclosing block ranges at an arbitrary offset, so the chain is built
    /// from the line and the whole document, which is what "expand selection"
    /// needs to keep growing.
    /// </summary>
    private object? HandleSelectionRange(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<SelectionRange>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<SelectionRange>();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<SelectionRange>();
        if (!parameters.TryGetProperty("positions", out var positions) || positions.ValueKind != JsonValueKind.Array)
            return Array.Empty<SelectionRange>();

        ISystemVerilogCodeDocument doc = file.CodeDocument;
        List<SelectionRange> result = new();
        foreach (JsonElement p in positions.EnumerateArray())
        {
            int line = p.TryGetProperty("line", out var lp) ? lp.GetInt32() : 0;
            int character = p.TryGetProperty("character", out var cp) ? cp.GetInt32() : 0;
            if (line < 0) line = 0;
            if (line >= doc.LineCount) line = doc.LineCount - 1;
            if (line < 0) { result.Add(new SelectionRange()); continue; }

            int lineLength = doc.GetLineLength(line);
            if (character < 0) character = 0;
            if (character > lineLength) character = lineLength;

            // innermost: the word under the caret (if any)
            int lineStart = doc.GetLineStartIndex(line);
            int index = lineStart + character;
            SelectionRange? current = null;
            if (doc.TryGetWord(index, out int wordStart, out int wordLength) && wordLength > 0)
            {
                current = new SelectionRange
                {
                    Range = new LspRange
                    {
                        Start = new LspPosition { Line = line, Character = wordStart - lineStart },
                        End = new LspPosition { Line = line, Character = wordStart - lineStart + wordLength },
                    },
                };
            }

            // then the whole line
            SelectionRange lineRange = new SelectionRange
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = line, Character = 0 },
                    End = new LspPosition { Line = line, Character = lineLength },
                },
            };
            if (current == null) current = lineRange;
            else current.Parent = lineRange;

            // then the whole document
            int lastLine = doc.LineCount - 1;
            lineRange.Parent = new SelectionRange
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = 0, Character = 0 },
                    End = new LspPosition { Line = lastLine, Character = doc.GetLineLength(lastLine) },
                },
            };

            result.Add(current);
        }
        return result;
    }

    /// <summary>
    /// textDocument/documentLink — a link on the file name of every
    /// <c>`include "..."</c> directive, resolved against the workspace roots.
    /// Unresolvable includes are skipped (the parser already reports them as
    /// diagnostics).
    /// </summary>
    private object? HandleDocumentLink(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<DocumentLink>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<DocumentLink>();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<DocumentLink>();

        ISystemVerilogCodeDocument doc = file.CodeDocument;
        List<DocumentLink> links = new();
        for (int line = 0; line < doc.LineCount; line++)
        {
            string text = doc.GetLineText(line);
            int at = text.IndexOf("`include", StringComparison.Ordinal);
            if (at < 0) continue;

            int quote = text.IndexOf('"', at);
            if (quote < 0) continue;
            int close = text.IndexOf('"', quote + 1);
            if (close < 0) continue;

            string includeName = text.Substring(quote + 1, close - quote - 1);
            string? target = ResolveIncludePath(project, file, includeName);
            if (target == null) continue;

            links.Add(new DocumentLink
            {
                Range = new LspRange
                {
                    Start = new LspPosition { Line = line, Character = quote + 1 },
                    End = new LspPosition { Line = line, Character = close },
                },
                Target = PathToUri(target),
            });
        }
        return links;
    }

    /// <summary>
    /// Resolves an <c>`include</c> target to an absolute path: first relative to
    /// the including file's directory, then under each workspace root. Null when
    /// the file does not exist.
    /// </summary>
    private string? ResolveIncludePath(InMemoryProject project, InMemoryFile file, string includeName)
    {
        string normalized = includeName.Replace('\\', '/');

        string? dir = Path.GetDirectoryName(file.AbsolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        foreach (string root in project.WorkspaceRoots)
        {
            string candidate = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// textDocument/semanticTokens/range — the same tokens as
    /// <c>semanticTokens/full</c>, filtered to the requested line range. The
    /// relative encoding is rebuilt from the filtered set so the deltas stay
    /// correct.
    /// </summary>
    private object? HandleSemanticTokensRange(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return new SemanticTokens();
        if (!td.TryGetProperty("uri", out var uriProp)) return new SemanticTokens();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return new SemanticTokens();

        int rangeStartLine = 0;
        int rangeEndLine = int.MaxValue;
        if (parameters.TryGetProperty("range", out var range)
            && range.TryGetProperty("start", out var rs)
            && range.TryGetProperty("end", out var re))
        {
            rangeStartLine = rs.TryGetProperty("line", out var sl) ? sl.GetInt32() : 0;
            rangeEndLine = re.TryGetProperty("line", out var el) ? el.GetInt32() : int.MaxValue;
        }

        List<TokenInfo> tokens = file.GetTokens();
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        ISystemVerilogCodeDocument doc = file.CodeDocument;

        List<int> data = new();
        int prevLine = 0;
        int prevChar = 0;
        foreach (TokenInfo t in tokens)
        {
            int startLine = doc.GetLineAt(t.Start);
            if (startLine < rangeStartLine || startLine > rangeEndLine) continue;

            int startChar = t.Start - doc.GetLineStartIndex(startLine);
            int endLine = doc.GetLineAt(t.End);
            int endChar = (endLine == startLine) ? t.End - doc.GetLineStartIndex(endLine)
                : doc.GetLineLength(startLine);
            if (endChar <= startChar) continue;

            int deltaLine = startLine - prevLine;
            int deltaChar = (startLine == prevLine) ? startChar - prevChar : startChar;
            data.Add(deltaLine);
            data.Add(deltaChar);
            data.Add(endChar - startChar);
            data.Add((int)t.Type);
            data.Add(0);
            prevLine = startLine;
            prevChar = startChar;
        }
        return new SemanticTokens { Data = data };
    }

    /// <summary>
    /// workspace/symbol — every declaration across the workspace whose name
    /// contains the query (case-insensitive). Files are parsed lazily, so the
    /// first call may build the symbol tables of the whole workspace.
    /// </summary>
    private object? HandleWorkspaceSymbol(JsonElement parameters, CancellationToken cancellationToken)
    {
        string query = parameters.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() ?? string.Empty
            : string.Empty;

        InMemoryProject project = _core.GetOrCreateProject("default");
        List<WorkspaceSymbol> symbols = new();
        foreach (ISystemVerilogFile file in project.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is not InMemoryFile mem) continue;

            foreach (InMemoryElement e in mem.GetDeclarationElements())
            {
                if (e.DefinitionRange is not { } range) continue;
                if (query.Length > 0
                    && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                symbols.Add(new WorkspaceSymbol
                {
                    Name = e.Name,
                    Kind = ToSymbolKind(e.Kind),
                    Location = ToLocation(mem, range),
                });
            }
        }
        return symbols;
    }

    /// <summary>workspace/executeCommand — returns null (no command handled) yet.</summary>
    private object? HandleExecuteCommand(JsonElement parameters, CancellationToken cancellationToken)
    {
        return null;
    }

    // ---------------------- notifications ----------------------

    /// <summary>
    /// workspace/didChangeWatchedFiles — files created / changed / deleted on
    /// disk. Re-scans the workspace roots so the file table (and therefore the
    /// hierarchy view and cross-file resolution) reflects the change without
    /// waiting for an explicit rescan request.
    ///
    /// <para>
    /// The scan only refreshes the list of known files; newly discovered files
    /// are parsed lazily when a query touches them, so this stays cheap even on
    /// a large workspace. Open documents are left untouched (their buffer is
    /// authoritative).
    /// </para>
    /// </summary>
    private void HandleDidChangeWatchedFiles(JsonElement parameters, CancellationToken cancellationToken)
    {
        InMemoryProject project = _core.GetOrCreateProject("default");
        RescanWorkspaceFiles(project);
    }

    /// <summary>workspace/didChangeConfiguration — settings changed. Not handled yet.</summary>
    private void HandleDidChangeConfiguration(JsonElement parameters, CancellationToken cancellationToken)
    {
    }

    /// <summary>textDocument/willSave — about to be saved. Not handled yet.</summary>
    private void HandleWillSave(JsonElement parameters, CancellationToken cancellationToken)
    {
    }
}
