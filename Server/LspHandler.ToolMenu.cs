using System.Text.Json;
using System.Threading;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// <c>rtl/toolMenu</c> — the tool menu (the desktop editor's Shift+Space list)
/// as data, so the VS Code extension can render it in a QuickPick.
///
/// <para>
/// The list itself lives in the plugin's UI-free
/// <c>CoreBridge.ToolMenuCatalog</c>, which the editor also iterates; a new
/// tool is added in one place. The availability of each row (whether the tool
/// applies at the caret) is decided by <c>CoreBridge.ToolMenuAdapter</c>, which
/// reuses the snippet code, so the menu and the tools cannot disagree.
/// </para>
///
/// <para>
/// Params: <c>{ "textDocument": { "uri": ... }, "position": { line, character },
/// "selection": { "start": { line, character }, "end": { line, character } } }</c>.
/// <c>selection</c> is optional; it decides whether the selection-based tools
/// (port inversion, case transforms) are enabled.
/// </para>
/// </summary>
public sealed partial class LspHandler
{
    /// <summary>
    /// Returns the tool menu rows for the caret, each with an <c>enabled</c>
    /// flag and, when disabled, the reason.
    /// </summary>
    private object? HandleToolMenu(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return new ToolMenuResultDto();
        if (!td.TryGetProperty("uri", out var uriProp)) return new ToolMenuResultDto();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        InMemoryFile? file = TryLoadFileFromDisk(project, uri);
        if (file == null)
        {
            return new ToolMenuResultDto { Message = "the file is not known to the server" };
        }

        int index = ResolveIndex(file, parameters, "position");
        int selectionStart = ResolveIndex(file, parameters, "selection", "start");
        int selectionEnd = ResolveIndex(file, parameters, "selection", "end");

        pluginVerilog.CoreBridge.ToolMenuAdapter.ToolMenuResult? result =
            file.GetToolMenu(index, selectionStart, selectionEnd);
        if (result == null)
        {
            return new ToolMenuResultDto { Message = "the file has not been parsed yet" };
        }

        ToolMenuResultDto dto = new ToolMenuResultDto { Message = result.Message };
        foreach (pluginVerilog.CoreBridge.ToolMenuAdapter.ToolMenuEntry entry in result.Entries)
        {
            dto.Entries.Add(new ToolMenuEntryDto
            {
                Id = entry.Id,
                Label = entry.Label,
                Detail = entry.Detail,
                Enabled = entry.Enabled,
                Reason = entry.Reason,
                DesktopOnly = entry.DesktopOnly,
            });
        }
        return dto;
    }
}
