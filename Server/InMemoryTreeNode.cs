using System;
using System.Collections.Generic;
using System.IO;
using SystemVerilogCore;
using SystemVerilogCore.Documents;
using SystemVerilogCore.Parsing;
using SystemVerilogCore.Tree;
using SystemVerilogCore.Verilog.BuildingBlocks;
using ModuleInstantiation = SystemVerilogCore.Verilog.Items.ModuleInstantiation;
using InterfaceInstantiation = SystemVerilogCore.Verilog.Items.InterfaceInstance;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// One node of the language server's instance tree.
///
/// <para>
/// The tree's <b>content</b> is decided by
/// <see cref="SystemVerilogCore.Tree.VerilogTreeBuilder"/>; this type only holds
/// the facts the builder asks for and the facts the client needs to render a
/// node (kind, label, paths, click position). Keeping the content rules in the
/// UI-free assembly is what lets the desktop editor and this host walk one rule
/// set instead of two implementations that drift.
/// </para>
/// </summary>
internal sealed class InMemoryTreeNode : IVerilogTreeNode
{
    private readonly List<IVerilogTreeNode> children = new();

    public VerilogTreeNodeKind Kind { get; init; }

    public string Name { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string? InstanceKey { get; init; }

    /// <summary>
    /// The node's parse result.
    ///
    /// <para>
    /// For a file this is the file's own parse. For an instance it stays null
    /// until the node is expanded: the instance has to be parsed AS ITSELF (with
    /// the parameter overrides written at the instantiation) to know which
    /// instantiations it contains, and doing that for every node up front would
    /// parse the whole design eagerly. The expand path fills it in
    /// (<see cref="SetParsedResult"/>), exactly like the editor.
    /// </para>
    /// </summary>
    public IParsedDocument? ParsedResult
    {
        get => parsedResult;
        init => parsedResult = value;
    }

    private IParsedDocument? parsedResult;

    /// <summary>Stores the parse result an expansion produced.</summary>
    public void SetParsedResult(IParsedDocument? parsed) => parsedResult = parsed;

    public IReadOnlyList<IVerilogTreeNode> Children => children;

    /// <summary>
    /// The file this node's parse result comes from.
    ///
    /// <para>
    /// Carried as the file itself rather than looked up by
    /// <see cref="RelativePath"/>: an in-memory file's relative path is its id
    /// ("mem://top.sv"), which is not a project-relative path, so a path lookup
    /// misses it and the node would silently expand to nothing.
    /// </para>
    /// </summary>
    public InMemoryFile? SourceFile { get; init; }

    /// <summary>
    /// The building block this node was parsed as. For a file it is null (the
    /// file lists its own blocks); for an instance it is the instantiated
    /// module / interface, which is what the builder lists children from.
    /// </summary>
    public string? BuildingBlockName { get; init; }

    /// <summary>The instantiation this node stands for, when it is a module instance.</summary>
    public ModuleInstantiation? ModuleInstantiation { get; init; }

    /// <summary>The instantiation this node stands for, when it is an interface instance.</summary>
    public InterfaceInstantiation? InterfaceInstantiation { get; init; }

    /// <summary>
    /// The building block this node instantiates, when it resolves. Used for the
    /// icon (an interface instantiated through the module path still shows the
    /// interface icon) and for the click position (the declaration of that block
    /// in its own file).
    /// </summary>
    public BuildingBlock? InstancedBuildingBlock { get; init; }

    /// <summary>
    /// The instantiation this node stands for, as the parser's interface.
    ///
    /// <para>
    /// The click path needs the instantiated name and the parameter overrides to
    /// re-parse the file as that instance. Both instantiations implement this
    /// interface, so the caller does not have to switch on the node kind or name
    /// the parser's concrete instantiation types.
    /// </para>
    /// </summary>
    public SystemVerilogCore.Verilog.Items.IBuildingBlockInstantiation? InstantiatingItem =>
        (SystemVerilogCore.Verilog.Items.IBuildingBlockInstantiation?)ModuleInstantiation
        ?? InterfaceInstantiation;

    /// <summary>Adds a child, replacing any child of the same name.</summary>
    public void AddChild(IVerilogTreeNode child)
    {
        if (child == null) return;
        for (int i = 0; i < children.Count; i++)
        {
            if (!string.Equals(children[i].Name, child.Name, StringComparison.Ordinal)) continue;
            children[i] = child;
            return;
        }
        children.Add(child);
    }

    /// <summary>Adds every child in order.</summary>
    public void AddChildren(IEnumerable<IVerilogTreeNode> items)
    {
        if (items == null) return;
        foreach (IVerilogTreeNode item in items) AddChild(item);
    }
}

/// <summary>
/// Creates this host's tree nodes for
/// <see cref="SystemVerilogCore.Tree.VerilogTreeBuilder"/>.
///
/// <para>
/// The builder decides which nodes exist; this factory decides what they are and
/// where their parse results come from. Unlike the editor, a node here is not a
/// file the host can open - the client opens the source file the node's
/// <see cref="IVerilogTreeNode.RelativePath"/> points at - so a node only carries
/// what the tree needs.
/// </para>
/// </summary>
internal sealed class InMemoryTreeNodeFactory : IVerilogTreeNodeFactory
{
    private readonly InMemoryProject project;

    public InMemoryTreeNodeFactory(InMemoryProject project)
    {
        this.project = project;
    }

    /// <summary>
    /// Looks an existing child up by name. The builder only reuses it when the
    /// instance key matches, so a different parameter override produces a new
    /// node instead of showing the previous one's children.
    /// </summary>
    public IVerilogTreeNode? FindChild(IVerilogTreeNode parent, string key)
    {
        foreach (IVerilogTreeNode child in parent.Children)
        {
            if (string.Equals(child.Name, key, StringComparison.Ordinal)) return child;
        }
        return null;
    }

    public IVerilogTreeNode? CreateModuleInstance(
        IVerilogTreeNode parent,
        ModuleInstantiation instantiation)
    {
        BuildingBlock? instanced = TryResolve(instantiation);
        if (instanced == null) return null;

        InMemoryFile? sourceFile = FindFile(instanced);
        return new InMemoryTreeNode
        {
            Kind = instanced is Interface
                ? VerilogTreeNodeKind.InterfaceInstance
                : VerilogTreeNodeKind.ModuleInstance,
            Name = string.IsNullOrEmpty(instantiation.Name)
                ? instantiation.SourceName
                : instantiation.Name,
            RelativePath = RelativePathOf(sourceFile) ?? instanced.File?.FileRelativePath ?? string.Empty,
            InstanceKey = InstanceKey(instantiation, instanced),
            // Lazily parsed on expand: see InMemoryTreeNode.ParsedResult.
            ParsedResult = null,
            SourceFile = sourceFile,
            BuildingBlockName = instantiation.SourceName,
            ModuleInstantiation = instantiation,
            InstancedBuildingBlock = instanced,
        };
    }

    public IReadOnlyList<IVerilogTreeNode>? CreateModuleInstanceArray(
        IVerilogTreeNode parent,
        ModuleInstantiation instantiation)
    {
        // An array's elements share one parse result and one source file; only
        // the index differs, which the name carries ("u[0]").
        BuildingBlock? instanced = TryResolve(instantiation);
        if (instanced == null) return null;

        int count = instantiation.InstanceCount;
        if (count <= 0) return null;

        string key = InstanceKey(instantiation, instanced) ?? instantiation.Name;
        InMemoryFile? sourceFile = FindFile(instanced);
        string relativePath = RelativePathOf(sourceFile)
            ?? instanced.File?.FileRelativePath
            ?? string.Empty;

        List<IVerilogTreeNode> nodes = new();
        for (int i = 0; i < count; i++)
        {
            nodes.Add(new InMemoryTreeNode
            {
                Kind = instanced is Interface
                    ? VerilogTreeNodeKind.InterfaceInstance
                    : VerilogTreeNodeKind.ModuleInstance,
                Name = instantiation.Name + "[" + i + "]",
                RelativePath = relativePath,
                InstanceKey = key + "[" + i + "]",
                ParsedResult = null,
                SourceFile = sourceFile,
                BuildingBlockName = instantiation.SourceName,
                ModuleInstantiation = instantiation,
                InstancedBuildingBlock = instanced,
            });
        }
        return nodes;
    }

    public IVerilogTreeNode? CreateInterfaceInstance(
        IVerilogTreeNode parent,
        InterfaceInstantiation instantiation)
    {
        BuildingBlock? instanced = TryResolveInterface(instantiation);
        InMemoryFile? sourceFile = instanced == null ? null : FindFile(instanced);
        return new InMemoryTreeNode
        {
            Kind = VerilogTreeNodeKind.InterfaceInstance,
            Name = string.IsNullOrEmpty(instantiation.Name)
                ? instantiation.SourceName
                : instantiation.Name,
            RelativePath = RelativePathOf(sourceFile) ?? instanced?.File?.FileRelativePath ?? string.Empty,
            InstanceKey = instantiation.OverrideParameterID,
            ParsedResult = null,
            SourceFile = sourceFile,
            BuildingBlockName = instantiation.SourceName,
            InterfaceInstantiation = instantiation,
            InstancedBuildingBlock = instanced,
        };
    }

    /// <summary>
    /// The node for a building block a multi-block file lists directly. This host
    /// models it as an instance of the block itself (the editor does the same), so
    /// the block's own instantiations nest under it.
    /// </summary>
    public IVerilogTreeNode? CreateBuildingBlockNode(
        IVerilogTreeNode parent,
        BuildingBlock block)
    {
        if (block == null || string.IsNullOrEmpty(block.Name)) return null;

        InMemoryFile? sourceFile = FindFile(block);
        return new InMemoryTreeNode
        {
            Kind = block is Interface
                ? VerilogTreeNodeKind.InterfaceInstance
                : VerilogTreeNodeKind.ModuleInstance,
            Name = block.Name,
            RelativePath = RelativePathOf(sourceFile) ?? block.File?.FileRelativePath ?? string.Empty,
            InstanceKey = null,
            ParsedResult = ParsedResultOf(sourceFile),
            SourceFile = sourceFile,
            BuildingBlockName = block.Name,
            InstancedBuildingBlock = block,
        };
    }

    public IVerilogTreeNode? CreateImportedPackage(IVerilogTreeNode parent, string packageName)
    {
        if (string.IsNullOrEmpty(packageName)) return null;

        InMemoryFile? sourceFile = FindFileByName(packageName);
        return new InMemoryTreeNode
        {
            Kind = VerilogTreeNodeKind.ImportedPackage,
            Name = packageName,
            RelativePath = RelativePathOf(sourceFile) ?? string.Empty,
            ParsedResult = ParsedResultOf(sourceFile),
            SourceFile = sourceFile,
            BuildingBlockName = packageName,
        };
    }

    public IVerilogTreeNode? CreateHeader(IVerilogTreeNode parent, IVerilogIncludeFile include)
    {
        if (include == null) return null;

        string relativePath = include.SourceFile?.FileRelativePath ?? include.IncludeId;
        InMemoryFile? sourceFile = FindFileByRelativePath(relativePath);
        string name = Path.GetFileName(relativePath);
        return new InMemoryTreeNode
        {
            Kind = VerilogTreeNodeKind.Header,
            Name = string.IsNullOrEmpty(name) ? relativePath : name,
            RelativePath = relativePath,
            ParsedResult = ParsedResultOf(sourceFile) ?? include.IncludeParsedDocument,
            SourceFile = sourceFile,
        };
    }

    /// <summary>
    /// The project-relative path of a file, read through the parser contract
    /// (<see cref="InMemoryFile"/> implements it explicitly).
    /// </summary>
    private static string? RelativePathOf(InMemoryFile? file)
    {
        return (file as SystemVerilogCore.Projects.IVerilogItem)?.RelativePath;
    }

    /// <summary>The file's parse result.</summary>
    private static IParsedDocument? ParsedResultOf(InMemoryFile? file)
    {
        return file?.ParsedDocument;
    }

    /// <summary>
    /// The building block an instantiation refers to, falling back to the source
    /// file's own (base) parse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetInstancedBuildingBlock</c> resolves the PARAMETER-OVERRIDDEN parse,
    /// which only exists once that instance has been parsed on demand. Without
    /// overrides the key differs from the base parse's key
    /// (<c>KeyGenerator(file, module, {})</c> vs <c>KeyGenerator(file, null, null)</c>),
    /// so it returns null for a perfectly resolvable module - and the node would
    /// silently disappear from the tree.
    /// </para>
    /// <para>
    /// The fallback is the same one the editor's instance creation uses: the
    /// definition namespace's file for that name, and the block of that name in
    /// its parse result. Only the child list is affected (a `generate if` branch
    /// would need the override, and the overridden parse is used when it exists).
    /// </para>
    /// </remarks>
    private static BuildingBlock? TryResolve(ModuleInstantiation instantiation)
    {
        try
        {
            BuildingBlock? instanced = instantiation.GetInstancedBuildingBlock();
            if (instanced != null) return instanced;
        }
        catch
        {
            // An unresolved cross-project instance can throw while resolving.
            // The tree must not fail over one node.
        }
        return ResolveFromDefinitionNameSpace(instantiation.Project, instantiation.SourceName);
    }

    private static BuildingBlock? TryResolveInterface(InterfaceInstantiation instantiation)
    {
        try
        {
            BuildingBlock? instanced = instantiation.GetInstancedBuildingBlock();
            if (instanced != null) return instanced;
        }
        catch
        {
        }
        return ResolveFromDefinitionNameSpace(instantiation.Project, instantiation.SourceName);
    }

    /// <summary>
    /// The named building block from the definition namespace of the project the
    /// instantiation resolves against, or null.
    /// </summary>
    private static BuildingBlock? ResolveFromDefinitionNameSpace(
        SystemVerilogCore.Projects.IVerilogProject? project, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        try
        {
            if (project?.Settings is not SystemVerilogCore.Verilog.IVerilogParserSettings settings) return null;

            SystemVerilogCore.Verilog.IVerilogNameSpaceFile? file =
                settings.DefinitionNameSpace.GetFile(name);
            if (file?.ParsedResult is not SystemVerilogCore.Verilog.ParsedDocument parsed) return null;

            return parsed.Root?.BuildingBlocks.TryGetValue(name, out BuildingBlock? block) == true
                ? block
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The parse-cache key an instance resolves under (module name plus parameter
    /// overrides), so the builder can tell two instances of the same module apart.
    /// </summary>
    private static string? InstanceKey(ModuleInstantiation instantiation, BuildingBlock instanced)
    {
        IVerilogSourceFile? sourceFile = instanced.File;
        if (sourceFile == null) return null;
        return SystemVerilogCore.Verilog.ParsedDocument.KeyGenerator(
            sourceFile, instantiation.SourceName, instantiation.ParameterOverrides);
    }

    private InMemoryFile? FindFile(BuildingBlock block)
    {
        return FindFileByRelativePath(block.File?.FileRelativePath);
    }

    private InMemoryFile? FindFileByName(string name)
    {
        foreach (ISystemVerilogFile file in project.Files)
        {
            if (file is not InMemoryFile mem) continue;
            string fileName = Path.GetFileName(mem.AbsolutePath ?? mem.Id);
            if (string.Equals(fileName, name, StringComparison.Ordinal)) return mem;
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(fileName), name, StringComparison.Ordinal))
            {
                return mem;
            }
        }
        return null;
    }

    private InMemoryFile? FindFileByRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (ISystemVerilogFile file in project.Files)
        {
            if (file is not InMemoryFile mem) continue;
            string? candidate = project.GetProjectRelativePath(mem.AbsolutePath);
            if (candidate == null) continue;
            if (string.Equals(
                    candidate.Replace('\\', '/').TrimStart('/'), normalized, StringComparison.Ordinal))
            {
                return mem;
            }
        }
        return null;
    }
}
