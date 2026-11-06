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

        // ---- presentation metadata (Phase 1: the server decides, the client
        // ---- only maps it onto the VS Code TreeItem) ----

        /// <summary>
        /// VS Code ThemeIcon (codicon) id: "folder" / "file" / "file-code" /
        /// "symbol-class" / "symbol-interface" / "symbol-package" / "warning".
        /// The client passes it straight to <c>new ThemeIcon(id, color)</c>.
        ///
        /// <para>
        /// Codicons are used instead of the plugin's own SVG assets because
        /// those are filled with a fixed dark grey (#4b4b4b) and would be
        /// invisible on a dark VS Code theme; codicons follow the theme.
        /// </para>
        /// </summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        /// <summary>
        /// Optional VS Code theme colour id for the icon ("errorForeground" /
        /// "editorWarning.foreground"), used to flag a node whose file has
        /// errors / warnings. Null means "use the theme's default icon colour".
        /// </summary>
        [JsonPropertyName("iconColor")]
        public string? IconColor { get; set; }

        /// <summary>
        /// <c>TreeItem.contextValue</c> ("rtlFolder" / "rtlFile" /
        /// "rtlInstance" / "rtlInfo"). Drives the right-click menu <c>when</c>
        /// clauses, so the client never has to derive it from <see cref="Kind"/>.
        /// </summary>
        [JsonPropertyName("contextValue")]
        public string? ContextValue { get; set; }

        /// <summary>
        /// <c>TreeItemCollapsibleState</c> as a string: "none" or "collapsed".
        /// </summary>
        [JsonPropertyName("collapsible")]
        public string? Collapsible { get; set; }

        /// <summary>
        /// <c>TreeItem.tooltip</c>. Built here so the client does not have to
        /// concatenate the instance path and the type.
        /// </summary>
        [JsonPropertyName("tooltip")]
        public string? Tooltip { get; set; }

        /// <summary>
        /// Readable instance path ("u_mid.u_leaf") for "Copy Instance Path".
        /// Empty for folder / file nodes.
        /// </summary>
        [JsonPropertyName("instancePath")]
        public string? InstancePath { get; set; }

        /// <summary>
        /// Absolute path of the file the node represents (file / instance
        /// nodes). Used by the file operations (Add / Delete / Open in
        /// Explorer) so the client does not have to compute it.
        /// </summary>
        [JsonPropertyName("fsPath")]
        public string? FsPath { get; set; }

        /// <summary>
        /// Absolute directory the node lives in, for "Add Folder" / "Add Blank
        /// File": a folder node is its own directory, a file / instance uses
        /// the directory of its source file.
        /// </summary>
        [JsonPropertyName("dirPath")]
        public string? DirPath { get; set; }

        /// <summary>
        /// Opaque token identifying this node for the next expansion request.
        /// The client stores it and echoes it back verbatim as
        /// <c>expandToken</c>; it never parses it. Null for leaf nodes (nothing
        /// to expand).
        /// </summary>
        [JsonPropertyName("expandToken")]
        public string? ExpandToken { get; set; }
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

        /// <summary>
        /// True when expanding the node shows children. Resolved from the parse
        /// result so the client can draw the expand arrow before the children
        /// are requested (expansion is lazy, like the editor's NavigatePanel).
        /// </summary>
        public bool HasChildren { get; set; }

        /// <summary>Readable instance path ("u_mid.u_leaf"); empty for files.</summary>
        public string? InstancePath { get; set; }

        /// <summary>
        /// Instance path as name segments from the file root ("u_mid", "u_leaf").
        /// Used to build the opaque expand token: names stay valid when a
        /// sibling is added or removed, unlike the old index sequence.
        /// </summary>
        public List<string> InstancePathSegments { get; } = new();

        /// <summary>Absolute path of the file the node represents.</summary>
        public string? FsPath { get; set; }

        /// <summary>Absolute directory the node lives in (for Add operations).</summary>
        public string? DirPath { get; set; }

        /// <summary>
        /// VS Code codicon id for the node, decided from the instantiated
        /// building block kind (module / interface / package / include header).
        /// </summary>
        public string? Icon { get; set; }

        /// <summary>
        /// VS Code theme colour id when the node's file has errors / warnings,
        /// so the tree flags broken files the way the editor's NavigatePanel
        /// does with its exclamation badge.
        /// </summary>
        public string? IconColor { get; set; }

        public List<HierarchyNodeDescriptor> Children { get; } = new();
    }
}
