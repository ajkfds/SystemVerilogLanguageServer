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
        /// Stable icon name: "folder" / "file" / "module" / "interface" /
        /// "package" / "header" / "systemverilog-document" /
        /// "verilog-document" / "verilog-header-document".
        ///
        /// <para>
        /// The client maps the name onto the plugin's own SVG assets (shipped
        /// in the extension's icons/ folder, light + dark variants) so the tree
        /// matches the desktop editor's NavigatePanel. Names the client does
        /// not ship an SVG for fall back to a VS Code codicon.
        /// </para>
        /// </summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        /// <summary>
        /// Optional VS Code theme colour id for the icon ("errorForeground" /
        /// "editorWarning.foreground"), used to colour the plain icon when a
        /// badge-specific asset is not shipped. Kept for clients that do not
        /// know <see cref="Badge"/>; see <see cref="Badge"/> for the preferred
        /// representation.
        /// </summary>
        [JsonPropertyName("iconColor")]
        public string? IconColor { get; set; }

        /// <summary>
        /// Badge drawn on top of the icon, mirroring the editor's NavigatePanel
        /// node badges: <c>"error"</c> / <c>"warning"</c> (exclamation triangle,
        /// bottom-left) and <c>"dirty"</c> (shine, top-right, unsaved edits).
        /// Null when the node has nothing to flag.
        ///
        /// <para>
        /// The client resolves <c>icons/{icon}-{badge}.svg</c> (light/dark) and
        /// falls back to <c>icons/{icon}.svg</c>, so the badge is part of the
        /// asset rather than a colour: VS Code's <c>TreeItem.iconPath</c> can
        /// only hold one icon and one theme colour, so an overlay has to be
        /// composited into the SVG.
        /// </para>
        /// </summary>
        [JsonPropertyName("badge")]
        public string? Badge { get; set; }

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
        /// Instance path as name segments from the file root ("u_mid", "u_leaf").
        ///
        /// <para>
        /// The client echoes this back in <c>rtl/reparse</c> when the node is
        /// clicked, so the server can parse the document AS that instance (with
        /// its parameter overrides) instead of as the plain source file. The
        /// editor opens the instance, not the file, which is why the override
        /// values show up in its diagnostics / colouring.
        /// </para>
        /// </summary>
        [JsonPropertyName("instancePathSegments")]
        public List<string>? InstancePathSegments { get; set; }

        /// <summary>
        /// URI of the file the instantiation is WRITTEN IN (the parent). The
        /// client echoes it back in <c>rtl/reparse</c> together with
        /// <see cref="InstancePathSegments"/> so the server can walk the
        /// parent's instance tree and find the instance (the node's own
        /// <see cref="Uri"/> is the instantiated module's source, a different
        /// file). Null for folder / file nodes.
        /// </summary>
        [JsonPropertyName("parentUri")]
        public string? ParentUri { get; set; }

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

        /// <summary>
        /// File the node OPENS when clicked: the item's own file. For an
        /// instance that is the instantiated module's source (the editor's
        /// NavigatePanel opens the instance's source file too, not the file the
        /// instantiation is written in); for an imported package / include
        /// header it is the package / header file.
        /// </summary>
        public string? Uri { get; set; }

        /// <summary>
        /// Base document of <see cref="InstancePathSegments"/>: the file whose
        /// instance tree was walked to produce this node, i.e. the file the
        /// expansion request was made on (the root of the node chain), used to
        /// build the expand token. This is not the click target: clicking opens
        /// <see cref="Uri"/> (the instantiated module's own source), while
        /// expanding resolves the chain from this base file. Null for nodes that
        /// are not instances.
        ///
        /// <para>
        /// It must be the chain's ROOT file, not the immediate parent: the chain
        /// keeps the ancestors ("aaabb/SS"), so resolving it against the parent
        /// (mod2.v, which does not declare "aaabb") fails.
        /// </para>
        /// </summary>
        public string? ExpandUri { get; set; }

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

        /// <summary>
        /// Base document of <see cref="InstancePathSegments"/>, sent back by the
        /// client in <c>rtl/reparse</c> so the server walks the same instance
        /// tree the node was produced from and parses the document AS that
        /// instance (parameter overrides applied). Same base as
        /// <see cref="ExpandUri"/>: the file the expansion request was made on,
        /// not the immediate parent. Null for non-instance nodes.
        /// </summary>
        public string? ParentUri { get; set; }

        /// <summary>Absolute path of the file the node represents.</summary>
        public string? FsPath { get; set; }

        /// <summary>Absolute directory the node lives in (for Add operations).</summary>
        public string? DirPath { get; set; }

        /// <summary>
        /// Stable icon name for the node, decided from the instantiated
        /// building block kind (module / interface / package / include header).
        /// The client maps it onto the plugin's SVG assets.
        /// </summary>
        public string? Icon { get; set; }

        /// <summary>
        /// VS Code theme colour id when the node's file has errors / warnings
        /// and no badge asset matched. See <see cref="Badge"/>.
        /// </summary>
        public string? IconColor { get; set; }

        /// <summary>
        /// Badge to composite onto the icon: <c>"error"</c> / <c>"warning"</c>
        /// (exclamation triangle, bottom-left) or <c>"dirty"</c> (shine,
        /// top-right). Null when the node is clean. At most one badge is set,
        /// in the editor's priority order error &gt; warning &gt; dirty.
        /// </summary>
        public string? Badge { get; set; }

        public List<HierarchyNodeDescriptor> Children { get; } = new();
    }
}
