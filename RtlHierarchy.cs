using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SystemVerilogLanguageServer
{
    /// <summary>
    /// Body of the <c>rtl/hierarchy</c> custom request (NavigatePanel
    /// equivalent tree for the VS Code "RTL Hierarchy" view).
    /// </summary>
    internal sealed class HierarchyResult
    {
        [JsonPropertyName("nodes")]
        public List<HierarchyNode> Nodes { get; set; } = new();
    }

    /// <summary>
    /// One node of the RTL hierarchy tree (a file, or an instance inside a file).
    /// </summary>
    internal sealed class HierarchyNode
    {
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>
        /// <see cref="HierarchyNodeKind.Folder"/> / <see cref="HierarchyNodeKind.File"/>
        /// or <see cref="HierarchyNodeKind.Instance"/>.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = HierarchyNodeKind.Instance;

        /// <summary>
        /// Workspace-relative folder path of a <see cref="HierarchyNodeKind.Folder"/>
        /// node ("src/sub", no leading / trailing slash). The extension echoes it
        /// back as <c>folderPath</c> to expand the folder.
        /// </summary>
        [JsonPropertyName("folderPath")]
        public string? FolderPath { get; set; }

        /// <summary>File URI the node lives in (used to open it on click).</summary>
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }

        /// <summary>Workspace-relative path (files) or instantiated type (instances).</summary>
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("startLine")]
        public int? StartLine { get; set; }

        [JsonPropertyName("startCharacter")]
        public int? StartCharacter { get; set; }

        /// <summary>True when the node has children (expandable).</summary>
        [JsonPropertyName("hasChildren")]
        public bool HasChildren { get; set; }
    }

    internal static class HierarchyNodeKind
    {
        public const string Folder = "folder";
        public const string File = "file";
        public const string Instance = "instance";
    }

    /// <summary>
    /// Materialised node of the in-memory hierarchy. The LSP layer resolves a
    /// nodePath to this descriptor so the plugin instantiation types never
    /// leave the server assembly.
    /// </summary>
    internal sealed class HierarchyNodeDescriptor
    {
        public string Label { get; set; } = "";
        public string? Detail { get; set; }
        public string? Uri { get; set; }
        public int? StartLine { get; set; }
        public int? StartCharacter { get; set; }
        public List<HierarchyNodeDescriptor> Children { get; } = new();
    }
}
