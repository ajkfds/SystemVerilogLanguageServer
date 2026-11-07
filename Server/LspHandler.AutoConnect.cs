using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using SystemVerilogCore.AutoConnect;
using SystemVerilogCore.Documents;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// <c>rtl/autoConnect</c> / <c>rtl/autoConnectApply</c> — the auto-connect form
/// for a module instantiation.
///
/// <para>
/// The desktop editor shows this form as an Avalonia window
/// (<c>Views.AutoConnectWindow</c>): one row per port, with the signal the
/// editor would connect, and per-row accept / reject. A headless LSP server
/// cannot open that window, so the form is split in two:
/// </para>
///
/// <list type="bullet">
/// <item><c>rtl/autoConnect</c> returns the rows as data
/// (<see cref="AutoConnectModel"/>). The client renders them (the VS Code
/// extension uses a webview so the whole port list is visible at once, like
/// the desktop window).</item>
/// <item><c>rtl/autoConnectApply</c> takes the chosen connections and returns
/// the replacement text for the instantiation as a <c>TextEdit</c>.</item>
/// </list>
///
/// <para>
/// Both are non-standard requests (vendor prefix <c>rtl/</c>). The decision of
/// <b>which signal matches a port</b> stays in the plugin's UI-free
/// <c>CoreBridge.AutoConnectAdapter</c>, so the desktop editor and the LSP host
/// share one implementation.
/// </para>
/// </summary>
public sealed partial class LspHandler
{
    /// <summary>
    /// Returns the auto-connect rows for the instantiation at the caret.
    /// Params: <c>{ "textDocument": { "uri": ... }, "position": { line, character } }</c>.
    /// </summary>
    private object? HandleAutoConnect(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return new AutoConnectDto();
        if (!td.TryGetProperty("uri", out var uriProp)) return new AutoConnectDto();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        InMemoryFile? file = TryLoadFileFromDisk(project, uri);
        if (file == null) return new AutoConnectDto { Message = "the file is not known to the server" };

        int index = ResolveIndex(file, parameters, "position");
        AutoConnectModel? model = file.GetAutoConnectModel(index);
        if (model == null) return new AutoConnectDto { Message = "the file has not been parsed yet" };

        AutoConnectDto dto = new AutoConnectDto
        {
            ModuleName = model.ModuleName,
            InstanceName = model.InstanceName,
            Message = model.Message,
        };
        foreach (AutoConnectPort port in model.Ports)
        {
            AutoConnectPortDto row = new AutoConnectPortDto
            {
                Name = port.Name,
                Direction = port.Direction,
                PortGroup = port.PortGroup,
                CurrentConnection = port.CurrentConnection,
                IsSettled = port.IsSettled,
            };
            row.Candidates.AddRange(port.Candidates);
            dto.Ports.Add(row);
        }
        return dto;
    }

    /// <summary>
    /// Applies the chosen connections and returns the replacement edit for the
    /// instantiation.
    ///
    /// <para>
    /// Params: <c>{ "textDocument": { "uri": ... }, "position": { line, character },
    /// "selections": { "portName": "signalName" | null } }</c>. A port mapped to
    /// null / "" is left unconnected.
    /// </para>
    /// </summary>
    private object? HandleAutoConnectApply(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<TextEdit>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<TextEdit>();
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        InMemoryFile? file = TryLoadFileFromDisk(project, uri);
        if (file == null) return Array.Empty<TextEdit>();

        int index = ResolveIndex(file, parameters, "position");

        Dictionary<string, string?> selections = new(StringComparer.Ordinal);
        if (parameters.TryGetProperty("selections", out var selectionsProp)
            && selectionsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in selectionsProp.EnumerateObject())
            {
                selections[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }

        // The instantiation is replaced as a whole, so the edit range is the
        // instantiation's own source range. The adapter rebuilds the text with
        // the same indent the editor uses.
        string indent = GetLineIndent(file, index);
        string? replacement = file.ApplyAutoConnect(index, selections, indent);
        if (replacement == null) return Array.Empty<TextEdit>();

        if (!TryGetInstantiationRange(file, index, out int start, out int end))
        {
            return Array.Empty<TextEdit>();
        }

        return new[]
        {
            new TextEdit
            {
                Range = ToLspRange(file, new SystemVerilogRange(start, end - start)),
                NewText = replacement,
            },
        };
    }

    /// <summary>
    /// The source range of the module instantiation at <paramref name="index"/>
    /// (its first token through its terminating <c>;</c>).
    /// </summary>
    private static bool TryGetInstantiationRange(
        InMemoryFile file,
        int index,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        pluginVerilog.Verilog.ParsedDocument? parsed = file.ParsedDocument;
        if (parsed?.Root == null) return false;

        pluginVerilog.Verilog.BuildingBlocks.BuildingBlock? enclosing = parsed.GetBuildingBlockAt(index);
        if (enclosing == null) return false;

        pluginVerilog.Verilog.IndexReference iref =
            pluginVerilog.Verilog.IndexReference.Create(parsed, null!, index);

        foreach (pluginVerilog.Verilog.Items.IBuildingBlockInstantiation instance
                 in enclosing.GetBuildingBlockInstantiations())
        {
            if (instance is not pluginVerilog.Verilog.Items.ModuleInstantiation) continue;
            if (iref.IsSmallerThan(instance.BeginIndexReference)) continue;
            if (instance.LastIndexReference == null) continue;
            if (iref.IsGreaterThan(instance.LastIndexReference)) continue;

            start = instance.BeginIndexReference.Indexes[^1];
            end = instance.LastIndexReference.Indexes[^1] + 1;
            return end > start;
        }
        return false;
    }

    /// <summary>
    /// The leading whitespace of the line containing <paramref name="index"/>,
    /// reused as the indent for the rebuilt instantiation.
    /// </summary>
    private static string GetLineIndent(InMemoryFile file, int index)
    {
        ISystemVerilogCodeDocument doc = file.CodeDocument;
        int line = doc.GetLineAt(index);
        string text = doc.GetLineText(line);
        int i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return text.Substring(0, i);
    }
}

// ---------------------- DTOs ----------------------

/// <summary>One port row of the auto-connect form.</summary>
public sealed class AutoConnectPortDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "";
    [JsonPropertyName("portGroup")] public string? PortGroup { get; set; }
    [JsonPropertyName("currentConnection")] public string? CurrentConnection { get; set; }
    [JsonPropertyName("candidates")] public List<string> Candidates { get; } = new();
    [JsonPropertyName("isSettled")] public bool IsSettled { get; set; }
}

/// <summary>The auto-connect form.</summary>
public sealed class AutoConnectDto
{
    [JsonPropertyName("moduleName")] public string ModuleName { get; set; } = "";
    [JsonPropertyName("instanceName")] public string InstanceName { get; set; } = "";
    [JsonPropertyName("ports")] public List<AutoConnectPortDto> Ports { get; } = new();
    [JsonPropertyName("message")] public string? Message { get; set; }
}
