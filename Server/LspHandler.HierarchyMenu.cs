
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
        SystemVerilogCore.Documents.IVerilogSourceFile? file = ResolveHierarchyMenuFile(parameters);

        // The rows come from the catalog in SystemVerilogCore, and the builder
        // decides which of them apply to this node. The host only has to say
        // which node it is and (for the simulation row) how to collect the file
        // list, which walks the host's own item tree.
        SystemVerilogCore.Menus.NavigateNodeMenuContext context = new()
        {
            Kind = SystemVerilogCore.Menus.NavigateNodeMenuContext.KindFromName(kind),
            File = file,
            NodeLabel = ReadString(parameters, "label"),
            InstancePath = instancePath,
            WorkspaceRelativePath = ReadString(parameters, "relativePath"),
            FsPath = ReadString(parameters, "fsPath"),
            DirPath = ReadString(parameters, "fsPath") is string p && p.Length > 0
                ? System.IO.Path.GetDirectoryName(p)
                : null,
            SimulationSetupText = BuildSimulationSetupText,
        };

        SystemVerilogCore.Menus.NavigateNodeMenuBuilder.MenuResult result =
            SystemVerilogCore.Menus.NavigateNodeMenuBuilder.Build(
                context, SystemVerilogCore.Menus.HierarchyMenuCatalog.Hosts.VsCode);

        dto.Message = result.Message;
        foreach (SystemVerilogCore.Menus.NavigateNodeMenuBuilder.MenuEntry entry in result.Entries)
        {
            dto.Entries.Add(new HierarchyMenuEntryDto
            {
                Id = entry.Id,
                Label = entry.Label,
                Detail = entry.Detail,
                Enabled = entry.Enabled,
                Reason = entry.Reason,
                Group = entry.Group,
                Icon = entry.VsCodeIcon,
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
        string? argument = ReadString(parameters, "argument");
        string? kind = ReadString(parameters, "kind");
        SystemVerilogCore.Documents.IVerilogSourceFile? file = ResolveHierarchyMenuFile(parameters);

        // The catalog is the single source of truth for both hosts, so the entry
        // is run through the shared builder (the editor calls the same method).
        SystemVerilogCore.Menus.NavigateNodeMenuContext context = new()
        {
            Kind = SystemVerilogCore.Menus.NavigateNodeMenuContext.KindFromName(kind),
            File = file,
            NodeLabel = label,
            InstancePath = instancePath,
            WorkspaceRelativePath = relativePath,
            FsPath = fsPath,
            DirPath = string.IsNullOrEmpty(fsPath)
                ? null
                : System.IO.Path.GetDirectoryName(fsPath),
            SimulationSetupText = BuildSimulationSetupText,
        };

        SystemVerilogCore.Menus.NavigateNodeMenuBuilder.InvokeResult result =
            SystemVerilogCore.Menus.NavigateNodeMenuBuilder.Invoke(id, context, argument);

        return new HierarchyMenuInvokeResultDto
        {
            Title = result.Title,
            Text = result.Text,
            ClipboardText = result.ClipboardText,
            Prompt = result.Prompt,
            Operation = ToOperationDto(result.Operation),
            Message = result.Message,
        };
    }

    /// <summary>Maps the builder's operation onto the wire DTO.</summary>
    private static HierarchyMenuOperationDto? ToOperationDto(
        SystemVerilogCore.Menus.NavigateNodeMenuBuilder.ClientOperation? operation)
    {
        if (operation == null) return null;
        return new HierarchyMenuOperationDto
        {
            Kind = operation.Kind,
            Path = operation.Path,
            Content = operation.Content,
            Recursive = operation.Recursive,
        };
    }

    /// <summary>
    /// Resolves the plugin file the node belongs to. <c>uri</c> is the file the
    /// node opens (for an instance the instantiated module's source), so loading
    /// it from disk when it is not open keeps a node menu usable without having
    /// opened the file first.
    /// </summary>
    private SystemVerilogCore.Documents.IVerilogSourceFile? ResolveHierarchyMenuFile(JsonElement parameters)
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

        // The simulation setup walks the instance tree. This host answers that
        // walk from the UI-free builder (FindInstanceItem), so nothing has to be
        // prepared here - the old PrepareItemTree pass existed only because the
        // editor's Updater was the sole producer of the item tree.

        return file.GetPluginVerilogFilePublic();
    }

    /// <summary>
    /// The body of the "Simulation Setup (files)" row: the top module plus every
    /// file the simulator would compile, and the include paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one row whose answer a UI-free builder cannot produce: it
    /// walks the host's item tree (the editor's project / the server's
    /// workspace), so <c>NavigateNodeMenuContext.SimulationSetupText</c> carries
    /// this callback instead. The collection itself is the plugin's
    /// <c>SimulationSetup</c>, so the editor and VS Code report the same list
    /// rather than two implementations.
    /// </para>
    /// <para>
    /// Instance nodes are passed as the instance file (not the base file),
    /// because only the instance parse carries the parameter overrides written
    /// at the instantiation - the same reason the editor passes
    /// <c>VerilogModuleInstance</c>.
    /// </para>
    /// </remarks>
    private static string? BuildSimulationSetupText(
        SystemVerilogCore.Menus.NavigateNodeMenuContext context)
    {
        // SimulationSetup walks the plugin's item tree, so the contract file has
        // to be one of the plugin's items. The cast is the host's business: the
        // builder never sees it.
        pluginVerilog.Data.IVerilogRelatedFile? file =
            context.File as pluginVerilog.Data.IVerilogRelatedFile;
        if (file == null) return null;

        SystemVerilogCore.Verilog.ParsedDocument? parsedDocument =
            file.VerilogParsedResult as SystemVerilogCore.Verilog.ParsedDocument;
        if (parsedDocument?.Root == null) return null;

        // Pick the building block this node represents: the instance's own
        // module when known, otherwise the first non-class block (the same rule
        // SimulationSetup.Create uses for a plain file).
        string? buildingBlockName = context.NodeLabel;
        if (string.IsNullOrEmpty(buildingBlockName)
            || !parsedDocument.Root.BuildingBlocks.ContainsKey(buildingBlockName))
        {
            buildingBlockName = null;
            foreach (SystemVerilogCore.Verilog.BuildingBlocks.BuildingBlock block
                     in parsedDocument.Root.BuildingBlocks.Values)
            {
                if (block is SystemVerilogCore.Verilog.BuildingBlocks.Class) continue;
                if (block is SystemVerilogCore.Verilog.BuildingBlocks.InterfaceClass) continue;
                if (block is SystemVerilogCore.Verilog.BuildingBlocks.Package) continue;
                buildingBlockName = block.Name;
                break;
            }
        }
        if (buildingBlockName == null) return null;

        pluginVerilog.Data.SimulationSetup? setup;
        try
        {
            setup = pluginVerilog.Data.SimulationSetup.Create(file, buildingBlockName);
        }
        catch (Exception)
        {
            // A collection failure must not take the menu down; the row reports
            // itself as uncollectable instead.
            return null;
        }
        if (setup == null) return null;

        System.Text.StringBuilder sb = new();
        sb.Append("top: ").Append(setup.TopName ?? buildingBlockName).Append('\n');

        System.Collections.Generic.List<string> files = new();
        foreach (pluginVerilog.Data.IVerilogRelatedFile target in setup.GetAllFiles())
        {
            string? path = pluginVerilog.Data.SimulationSetup.GetAbsolutePath(target)
                ?? target.RelativePath;
            if (!string.IsNullOrEmpty(path)) files.Add(path);
        }
        sb.Append('\n').Append("files (").Append(files.Count).Append("):\n");
        foreach (string path in files) sb.Append("  ").Append(path).Append('\n');

        System.Collections.Generic.List<string> includePaths = setup.GetAllIncludePaths();
        if (includePaths.Count > 0)
        {
            sb.Append('\n').Append("include paths (").Append(includePaths.Count).Append("):\n");
            foreach (string path in includePaths) sb.Append("  ").Append(path).Append('\n');
        }

        if (setup.UnfoundModules.Count > 0)
        {
            sb.Append('\n').Append("unresolved (").Append(setup.UnfoundModules.Count).Append("):\n");
            foreach (string name in setup.UnfoundModules) sb.Append("  ").Append(name).Append('\n');
        }

        return sb.ToString();
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

    /// <summary>
    /// Icon the client can draw: an icon name under the extension's
    /// <c>icons/</c> folder, or a <c>$(...)</c> codicon. Null when the row has
    /// none.
    /// </summary>
    /// <remarks>
    /// Only the QuickPick fallback can use it - the native
    /// <c>view/item/context</c> menu has no icon property, so VS Code draws
    /// those rows as text only.
    /// </remarks>
    [System.Text.Json.Serialization.JsonPropertyName("icon")]
    public string? Icon { get; set; }
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

    /// <summary>
    /// Question the client should ask before the entry can run (a new folder /
    /// file / module name). The client shows an input box and calls
    /// <c>rtl/hierarchyMenuInvoke</c> again with <c>argument</c> set to the
    /// answer, exactly like the editor's input window.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>
    /// File-system work the client performs. The server decides what and where;
    /// the client uses <c>vscode.workspace.fs</c> so remote and virtual file
    /// systems keep working.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("operation")]
    public HierarchyMenuOperationDto? Operation { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// A file-system operation the client performs on the server's behalf:
/// <c>createFolder</c> / <c>createFile</c> / <c>delete</c> / <c>reveal</c>.
/// </summary>
public sealed class HierarchyMenuOperationDto
{
    /// <summary>Operation kind (see <see cref="HierarchyMenuOperationDto"/>).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Absolute path the operation acts on.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Initial content for <c>createFile</c>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Delete directories recursively.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recursive")]
    public bool Recursive { get; set; }
}
