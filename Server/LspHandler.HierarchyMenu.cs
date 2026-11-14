
using System.Text.Json;
using System.Threading;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// <c>rtl/hierarchyMenu</c> — the RTL Hierarchy node menu as data, so the
/// extension can offer menu entries that are defined in C#.
///
/// <para>
/// VS Code declares <c>contributes.menus."view/item/context"</c> statically in
/// <c>package.json</c>, so a menu row written in C# could never appear unless
/// the client also knew about it. Instead the client registers ONE static
/// command ("Show Menu…") and this request answers which rows apply to the
/// selected node: adding an entry to
/// <c>CoreBridge.HierarchyMenuCatalog</c> (or from another plugin through
/// <c>HierarchyMenuCatalog.Register</c>) makes it show up with no client
/// change at all.
/// </para>
///
/// <para>
/// Params: <c>{ "kind": "instance", "uri": "file:///...", "label": "u_leaf",
/// "instancePath": "u_mid.u_leaf" }</c>. <c>uri</c> is the file the node opens
/// (for an instance, the instantiated module's source), so the server can
/// resolve the parse result the entry needs.
/// </para>
/// </summary>
public sealed partial class LspHandler
{
    /// <summary>
    /// Returns the menu rows that apply to the node described in
    /// <paramref name="parameters"/>.
    /// </summary>
    private object? HandleHierarchyMenu(JsonElement parameters, CancellationToken cancellationToken)
    {
        HierarchyMenuResultDto dto = new();

        string kind = ReadString(parameters, "kind") ?? string.Empty;
        if (string.IsNullOrEmpty(kind))
        {
            dto.Message = "no node kind given";
            return dto;
        }

        string? instancePath = ReadString(parameters, "instancePath");
        pluginVerilog.Data.IVerilogRelatedFile? file = ResolveHierarchyMenuFile(parameters);

        pluginVerilog.CoreBridge.HierarchyMenuAdapter.MenuResult result =
            pluginVerilog.CoreBridge.HierarchyMenuAdapter.Build(kind, file, instancePath);

        dto.Message = result.Message;
        foreach (pluginVerilog.CoreBridge.HierarchyMenuAdapter.MenuEntry entry in result.Entries)
        {
            dto.Entries.Add(new HierarchyMenuEntryDto
            {
                Id = entry.Id,
                Label = entry.Label,
                Detail = entry.Detail,
                Enabled = entry.Enabled,
                Reason = entry.Reason,
                Group = entry.Group,
            });
        }
        return dto;
    }

    /// <summary>
    /// Runs one menu entry for the node and returns what the client should
    /// show (text to display, a path to reveal, or text to copy).
    /// </summary>
    private object? HandleHierarchyMenuInvoke(JsonElement parameters, CancellationToken cancellationToken)
    {
        string id = ReadString(parameters, "id") ?? string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            return new HierarchyMenuInvokeResultDto { Message = "no menu id given" };
        }

        string? label = ReadString(parameters, "label");
        string? instancePath = ReadString(parameters, "instancePath");
        string? fsPath = ReadString(parameters, "fsPath");
        string? relativePath = ReadString(parameters, "relativePath");
        pluginVerilog.Data.IVerilogRelatedFile? file = ResolveHierarchyMenuFile(parameters);

        pluginVerilog.CoreBridge.HierarchyMenuAdapter.InvokeResult result =
            pluginVerilog.CoreBridge.HierarchyMenuAdapter.Invoke(
                id, file, label, instancePath, relativePath, fsPath);

        return new HierarchyMenuInvokeResultDto
        {
            Title = result.Title,
            Text = result.Text,
            FsPath = result.FsPath,
            ClipboardText = result.ClipboardText,
            Message = result.Message,
        };
    }

    /// <summary>
    /// Resolves the plugin file the node belongs to. <c>uri</c> is the file the
    /// node opens (for an instance the instantiated module's source), so loading
    /// it from disk when it is not open keeps a node menu usable without having
    /// opened the file first.
    /// </summary>
    private pluginVerilog.Data.IVerilogRelatedFile? ResolveHierarchyMenuFile(JsonElement parameters)
    {
        string? uri = ReadString(parameters, "uri");
        if (string.IsNullOrEmpty(uri)) return null;

        string normalized = NormalizeUri(uri);
        InMemoryProject project = GetOrCreateProjectForUri(normalized);
        InMemoryFile? file = TryLoadFileFromDisk(project, normalized);
        if (file == null) return null;

        // Parsing is lazy (initialize only lists the files), so a request that
        // walks instantiations - the simulation setup does exactly that - would
        // see only the files opened so far and silently miss the dependencies.
        // Finish the workspace parse first, exactly like the hierarchy
        // expansion does.
        InMemoryFile.EnsureProjectBuilt(project);

        // The plugin item tree is only filled by the editor's Updater (the
        // hierarchy expansion runs it per level). SimulationSetup walks that
        // tree, so without this pass it would report only the top file and
        // silently miss the instantiated modules.
        file.PrepareItemTree();

        return file.GetPluginVerilogFilePublic();
    }

    /// <summary>Reads an optional string property, or null when absent / not a string.</summary>
    private static string? ReadString(JsonElement parameters, string property)
    {
        if (!parameters.TryGetProperty(property, out var element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;
        string? value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>Result of <c>rtl/hierarchyMenu</c>.</summary>
public sealed class HierarchyMenuResultDto
{
    /// <summary>Rows that apply to the node, in group order.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("entries")]
    public System.Collections.Generic.List<HierarchyMenuEntryDto> Entries { get; set; } = new();

    /// <summary>Why the menu is empty (only set when it is).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>One row of the hierarchy node menu.</summary>
public sealed class HierarchyMenuEntryDto
{
    /// <summary>Stable id, sent back to <c>rtl/hierarchyMenuInvoke</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary><c>false</c> when the entry cannot run for this node.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Why the entry cannot run (only set when disabled).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Ordering group.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("group")]
    public int Group { get; set; }
}

/// <summary>Result of <c>rtl/hierarchyMenuInvoke</c>.</summary>
public sealed class HierarchyMenuInvokeResultDto
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Body text to display (may be multi-line).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Absolute path to reveal in the OS file manager.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fsPath")]
    public string? FsPath { get; set; }

    /// <summary>Text to put on the clipboard.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("clipboardText")]
    public string? ClipboardText { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
}
