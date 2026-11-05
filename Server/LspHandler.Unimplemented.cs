using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

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

    /// <summary>textDocument/formatting — returns no edits yet.</summary>
    private object? HandleFormatting(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<TextEdit>();
    }

    /// <summary>textDocument/rangeFormatting — returns no edits yet.</summary>
    private object? HandleRangeFormatting(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<TextEdit>();
    }

    /// <summary>textDocument/onTypeFormatting — returns no edits yet.</summary>
    private object? HandleOnTypeFormatting(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<TextEdit>();
    }

    /// <summary>textDocument/rename — returns a null workspace edit yet.</summary>
    private object? HandleRename(JsonElement parameters, CancellationToken cancellationToken)
    {
        return null;
    }

    /// <summary>textDocument/prepareRename — returns null (nothing renameable) yet.</summary>
    private object? HandlePrepareRename(JsonElement parameters, CancellationToken cancellationToken)
    {
        return null;
    }

    /// <summary>textDocument/codeAction — returns no actions yet.</summary>
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

    /// <summary>textDocument/signatureHelp — returns null (no signature) yet.</summary>
    private object? HandleSignatureHelp(JsonElement parameters, CancellationToken cancellationToken)
    {
        return null;
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

    /// <summary>textDocument/declaration — returns no locations yet.</summary>
    private object? HandleDeclaration(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<Location>();
    }

    /// <summary>textDocument/typeDefinition — returns no locations yet.</summary>
    private object? HandleTypeDefinition(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<Location>();
    }

    /// <summary>textDocument/implementation — returns no locations yet.</summary>
    private object? HandleImplementation(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<Location>();
    }

    /// <summary>textDocument/documentHighlight — returns no highlights yet.</summary>
    private object? HandleDocumentHighlight(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<DocumentHighlight>();
    }

    /// <summary>textDocument/foldingRange — returns no ranges yet.</summary>
    private object? HandleFoldingRange(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<FoldingRange>();
    }

    /// <summary>textDocument/selectionRange — returns no ranges yet.</summary>
    private object? HandleSelectionRange(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<SelectionRange>();
    }

    /// <summary>textDocument/documentLink — returns no links yet.</summary>
    private object? HandleDocumentLink(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<DocumentLink>();
    }

    /// <summary>textDocument/semanticTokens/range — returns no tokens yet.</summary>
    private object? HandleSemanticTokensRange(JsonElement parameters, CancellationToken cancellationToken)
    {
        return new SemanticTokens();
    }

    /// <summary>workspace/symbol — returns no symbols yet.</summary>
    private object? HandleWorkspaceSymbol(JsonElement parameters, CancellationToken cancellationToken)
    {
        return Array.Empty<WorkspaceSymbol>();
    }

    /// <summary>workspace/executeCommand — returns null (no command handled) yet.</summary>
    private object? HandleExecuteCommand(JsonElement parameters, CancellationToken cancellationToken)
    {
        return null;
    }

    // ---------------------- notifications ----------------------

    /// <summary>
    /// workspace/didChangeWatchedFiles — files created / changed / deleted on
    /// disk. Not handled yet; the hierarchy view still picks changes up through
    /// its explicit rescan request.
    /// </summary>
    private void HandleDidChangeWatchedFiles(JsonElement parameters, CancellationToken cancellationToken)
    {
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
