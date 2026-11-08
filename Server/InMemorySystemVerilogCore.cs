using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SystemVerilogCore;
using SystemVerilogCore.Diagnostics;
using SystemVerilogCore.Documents;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// Reference implementation of <see cref="ISystemVerilogCore"/> that holds
/// the open files in memory. This is the minimum the LSP needs to start
/// talking to the core: it is the seam that
/// <c>CodeEditor2VerilogPlugin.SystemVerilogCoreAdapter</c> will replace
/// with a real, parser-backed implementation in a later phase.
/// </summary>
public sealed class InMemorySystemVerilogCore : ISystemVerilogCore
{
    private readonly ConcurrentDictionary<string, InMemoryProject> _projects = new();

    public Task<ISystemVerilogProject> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        InMemoryProject project = _projects.GetOrAdd(projectId, id => new InMemoryProject(id));
        return Task.FromResult<ISystemVerilogProject>(project);
    }

    internal InMemoryProject GetOrCreateProject(string projectId) =>
        _projects.GetOrAdd(projectId, id => new InMemoryProject(id));

    /// <summary>
    /// Test-only entry point that exposes the same per-project handle the
    /// LSP dispatcher uses internally, so external test projects can build
    /// up an in-memory file table without having to round-trip through
    /// <c>textDocument/didOpen</c>. The handle is also retrievable through
    /// <see cref="GetProjectAsync"/>; this overload just avoids the async
    /// ceremony.
    /// </summary>
    public ISystemVerilogProject GetOrCreateProjectPublic(string projectId) =>
        GetOrCreateProject(projectId);
}

/// <summary>
/// Minimal UI-free <see cref="CodeEditor2.Data.Project"/> stub. The Project
/// constructor is protected, so a derived class in this assembly calls it
/// without running Project.CreateAsync (which needs the Avalonia dispatcher).
/// One instance is shared by every file of an <see cref="InMemoryProject"/> so
/// that all files register into / resolve from the same plugin name spaces
/// (see <see cref="InMemoryProject.PluginProject"/>).
/// </summary>
internal sealed class ParserProjectStub : CodeEditor2.Data.Project
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public ParserProjectStub() : base("lsp", System.IO.Path.GetTempPath(), "") { }
}

internal sealed class InMemoryProject : ISystemVerilogProject
{
    private readonly ConcurrentDictionary<string, InMemoryFile> _files = new();

    /// <summary>
    /// The single plugin-side <see cref="CodeEditor2.Data.Project"/> shared by
    /// every file of this project.
    ///
    /// <para>
    /// The plugin resolves cross-file symbols through
    /// <c>ProjectProperty.DefinitionNameSpace</c> /
    /// <c>PackageNameSpace</c> / <c>UnitNameSpace</c>, and
    /// <c>VerilogFile.ProjectProperty</c> is looked up from
    /// <c>Project.ProjectProperties["Verilog"]</c>. Giving each file its own
    /// Project (and therefore its own ProjectProperty) means every file has a
    /// private set of name spaces, so a module declared in another file of the
    /// same workspace is never found ("unfound module"). Sharing one Project
    /// makes all files register into / resolve from the same name spaces.
    /// </para>
    /// </summary>
    internal CodeEditor2.Data.Project PluginProject { get; }

    /// <summary>
    /// Absolute workspace root folders (reported by the client at initialize).
    /// Used to render workspace-relative paths in the hierarchy tree.
    /// </summary>
    internal List<string> WorkspaceRoots { get; } = new();

    /// <summary>
    /// Plugin-side item tree registration, keyed by project-relative path.
    ///
    /// <para>
    /// The plugin resolves <c>`include</c> targets exclusively through the
    /// plugin <see cref="CodeEditor2.Data.Project"/>'s item tree
    /// (<c>Project.FindItems</c> / <c>Project.SearchFile</c> /
    /// <c>Project.GetItem</c>, see <c>WordScanner.parseInclude</c> and
    /// <c>VerilogHeaderInstance.Create</c>). The LSP host never builds that
    /// tree — it only keeps <see cref="InMemoryFile"/> entries — so every
    /// <c>`include</c> reported "file not found" even though the target had
    /// been loaded from disk. The editor builds the tree in
    /// <c>Folder.UpdateAsync</c>, which is why the same project resolves
    /// includes correctly in RtlEditor.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, CodeEditor2.Data.TextFile> _pluginItems =
        new(StringComparer.Ordinal);

    /// <summary>Guards <see cref="_pluginItems"/> and the plugin item tree.</summary>
    private readonly object _pluginItemLock = new();

    public InMemoryProject(string id)
    {
        Id = id;

        // register the plugin file types once (UI-free) so that
        // pluginVerilog.Plugin.StaticID / ProjectProperty are available
        if (CodeEditor2.Global.FileTypes.Count == 0)
        {
            new pluginVerilog.Plugin().Register();
        }

        PluginProject = new ParserProjectStub();
        // Normally registered by Plugin.projectCreated (fired from
        // Project.CreateAsync); the UI-free parse path bypasses it.
        if (!PluginProject.ProjectProperties.ContainsKey(pluginVerilog.Plugin.StaticID))
        {
            PluginProject.ProjectProperties.Add(
                pluginVerilog.Plugin.StaticID,
                new pluginVerilog.ProjectProperty(
                    PluginProject, new pluginVerilog.ProjectProperty.Setup()));
        }
    }

    public string Id { get; }

    public IReadOnlyList<ISystemVerilogFile> Files
    {
        get
        {
            List<ISystemVerilogFile> list = new(_files.Count);
            foreach (var f in _files.Values) list.Add(f);
            return list;
        }
    }

    public ISystemVerilogFile? FindFile(string id)
    {
        _files.TryGetValue(id, out var f);
        return f;
    }

    public InMemoryFile AddOrUpdateFile(string id, string? absolutePath, string text, bool isSystemVerilog)
    {
        InMemoryFile file = new(id, absolutePath, isSystemVerilog, text);
        file.ProjectOwner = this;
        _files[id] = file;
        // keep the plugin item tree in sync: `include resolution goes through
        // it (see RegisterPluginItem)
        RegisterPluginItem(file);
        return file;
    }

    /// <summary>
    /// Returns the plugin item tree entry for <paramref name="relativePath"/>
    /// (the <see cref="pluginVerilog.Data.VerilogFile"/> /
    /// <see cref="pluginVerilog.Data.VerilogHeaderFile"/> the editor's
    /// NavigatePanel update code operates on), or null when the file has not
    /// been registered (no workspace root yet).
    /// </summary>
    internal CodeEditor2.Data.TextFile? GetPluginItem(string relativePath)
    {
        lock (_pluginItemLock)
        {
            return _pluginItems.TryGetValue(relativePath, out CodeEditor2.Data.TextFile? item) ? item : null;
        }
    }

    /// <summary>
    /// Maps a workspace-relative path back to the document URI the LSP client
    /// uses, so a hierarchy node can open the file it lives in. Returns null
    /// when no loaded file matches (e.g. an include that was never opened).
    /// </summary>
    internal string? GetUriForRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        foreach (InMemoryFile file in _files.Values)
        {
            if (string.Equals(GetProjectRelativePath(file.AbsolutePath), relativePath, StringComparison.Ordinal))
            {
                return file.Id;
            }
        }
        return null;
    }

    /// <summary>
    /// Converts an absolute path into the project-relative path the plugin
    /// item tree is keyed by (workspace-relative, '/' separated). Returns null
    /// when the path is not under any workspace root (an untitled buffer, or a
    /// file opened before a root was known).
    /// </summary>
    internal string? GetProjectRelativePath(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return null;

        foreach (string root in WorkspaceRoots)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            if (absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return absolutePath.Substring(normalizedRoot.Length).Replace('\\', '/');
            }
        }
        return null;
    }

    /// <summary>
    /// Registers (or refreshes) the plugin-side item tree entry for
    /// <paramref name="file"/>.
    ///
    /// <para>
    /// <c>WordScanner.parseInclude</c> resolves an include target only through
    /// the plugin <see cref="CodeEditor2.Data.Project"/> item tree
    /// (<c>FindItems</c> / <c>SearchFile</c>) and
    /// <c>VerilogHeaderInstance.Create</c> only through
    /// <c>Project.GetItem</c>. The LSP host keeps its own
    /// <see cref="InMemoryFile"/> table and never built that tree, so every
    /// <c>`include</c> reported "file not found" although the target had been
    /// loaded from disk. The editor builds the tree in
    /// <c>Folder.UpdateAsync</c>, which is why the same project resolves
    /// includes in RtlEditor.
    /// </para>
    ///
    /// <para>
    /// The tree is keyed by the workspace-relative path with '/' separators
    /// (the form <c>parseInclude</c> compares against after normalising
    /// backslashes). <c>Item.GetItem</c> accepts both separators, so the
    /// lookup works on Windows and Linux alike.
    /// </para>
    /// </summary>
    internal void RegisterPluginItem(InMemoryFile file)
    {
        string? relativePath = GetProjectRelativePath(file.AbsolutePath);
        if (string.IsNullOrEmpty(relativePath)) return;

        lock (_pluginItemLock)
        {
            CodeEditor2.Data.TextFile textFile = GetOrCreatePluginItem(relativePath, file.IsSystemVerilog);
            SetPluginItemText(textFile, file.CodeDocumentText);
            // The file's text changed: the per-instance parse results this file
            // produced for its instances are now stale. Drop them so an expand
            // re-parses the source and shows the current instantiations (the
            // editor's re-parse loop does this; the LSP host has none).
            (textFile as pluginVerilog.Data.VerilogFile)?.ClearInstancedParsedDocuments();
        }
    }

    /// <summary>
    /// Re-registers every known file after the workspace roots changed (a root
    /// inferred from an opened file, or a folder added to the workspace). The
    /// relative path of a file depends on the root, so entries registered
    /// before the root was known must be re-keyed.
    /// </summary>
    internal void RebuildPluginItemTree()
    {
        lock (_pluginItemLock)
        {
            foreach (InMemoryFile file in _files.Values)
            {
                RegisterPluginItem(file);
            }
        }
    }

    /// <summary>
    /// Returns the plugin item for <paramref name="relativePath"/>, creating
    /// the intermediate folders and the file item on first use. The item type
    /// must be a <see cref="pluginVerilog.Data.VerilogFile"/> /
    /// <see cref="pluginVerilog.Data.VerilogHeaderFile"/>: the include parse
    /// snapshots the item's <c>CodeDocument</c> and requires
    /// <c>TextFile as IVerilogRelatedFile</c> to be non-null.
    /// </summary>
    private CodeEditor2.Data.TextFile GetOrCreatePluginItem(string relativePath, bool isSystemVerilog)
    {
        if (_pluginItems.TryGetValue(relativePath, out CodeEditor2.Data.TextFile? existing))
        {
            return existing;
        }

        string name = relativePath;
        string directory = string.Empty;
        int slash = relativePath.LastIndexOf('/');
        if (slash >= 0)
        {
            name = relativePath.Substring(slash + 1);
            directory = relativePath.Substring(0, slash);
        }

        CodeEditor2.Data.Folder parent = EnsurePluginFolder(directory);

        CodeEditor2.Data.TextFile item;
        if (name.EndsWith(".svh", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".vh", StringComparison.OrdinalIgnoreCase))
        {
            item = new pluginVerilog.Data.VerilogHeaderFile
            {
                Name = name,
                Project = PluginProject,
                RelativePath = relativePath,
            };
        }
        else
        {
            item = new pluginVerilog.Data.VerilogFile
            {
                Name = name,
                Project = PluginProject,
                RelativePath = relativePath,
                SystemVerilog = isSystemVerilog,
            };
        }

        parent.Items.AddOrUpdate(name, item);
        _pluginItems[relativePath] = item;
        return item;
    }

    /// <summary>
    /// Creates the intermediate folders for a workspace-relative directory
    /// ("a/b/c") under the plugin project and returns the deepest one.
    /// </summary>
    private CodeEditor2.Data.Folder EnsurePluginFolder(string directoryRelative)
    {
        if (string.IsNullOrEmpty(directoryRelative)) return PluginProject;

        CodeEditor2.Data.Item current = PluginProject;
        string accumulated = string.Empty;
        foreach (string part in directoryRelative.Split('/'))
        {
            if (part.Length == 0) continue;
            accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;

            if (current.Items.TryGetValue(part, out CodeEditor2.Data.Item? found) && found != null)
            {
                current = found;
                continue;
            }

            // Folder.Create derives Name from the platform separator, so pass
            // the accumulated path with that separator while keeping the item
            // key (and RelativePath) in the '/' form the include lookup uses.
            CodeEditor2.Data.Folder folder = CodeEditor2.Data.Folder.Create(
                accumulated.Replace('/', Path.DirectorySeparatorChar), PluginProject, current);
            current.Items.AddOrUpdate(part, folder);
            current = folder;
        }
        return (CodeEditor2.Data.Folder)current;
    }

    /// <summary>
    /// Assigns the text to the plugin item's <c>CodeDocument</c>. The setter is
    /// not public in a UI-less host (FileCheckAsync is UI dependent), so
    /// reflection is used — the same approach as
    /// <c>CoreBridge.ParseEngine</c> / the sv-tests harness.
    /// </summary>
    private static void SetPluginItemText(CodeEditor2.Data.TextFile textFile, string text)
    {
        pluginVerilog.CodeEditor.CodeDocument document =
            new pluginVerilog.CodeEditor.CodeDocument(text);

        System.Reflection.PropertyInfo? property = typeof(CodeEditor2.Data.TextFile).GetProperty(
            "CodeDocument",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(textFile, document);
    }

    /// <summary>
    /// Registers a workspace file, keeping the existing entry (and therefore
    /// its parse result) when the text did not change. Used by the workspace
    /// rescan: recreating every InMemoryFile on each refresh discarded the
    /// cached parse of the whole project and made a refresh re-parse
    /// everything.
    /// </summary>
    internal InMemoryFile AddOrUpdateFileIfChanged(string id, string? absolutePath, string text, bool isSystemVerilog)
    {
        if (_files.TryGetValue(id, out InMemoryFile? existing)
            && existing.AbsolutePath == absolutePath
            && existing.IsSystemVerilog == isSystemVerilog
            && existing.CodeDocumentText == text)
        {
            return existing;
        }
        return AddOrUpdateFile(id, absolutePath, text, isSystemVerilog);
    }

    /// <summary>
    /// Project-wide declaration lookup by name (cross-file definition
    /// resolution). Only files with the same language kind participate.
    /// </summary>
    internal InMemoryElement? FindDeclarationProjectWide(string name)
    {
        foreach (InMemoryFile f in _files.Values)
        {
            InMemoryElement? d = f.FindDeclarationByName(name);
            if (d != null) return d;
        }
        return null;
    }

    public void RemoveFile(string id)
    {
        if (_files.TryRemove(id, out InMemoryFile? removed))
        {
            RemovePluginItem(removed);
        }
    }

    /// <summary>
    /// Drops the plugin item tree entry of a file that left the project, so a
    /// later <c>`include</c> does not resolve to a stale item.
    /// </summary>
    private void RemovePluginItem(InMemoryFile file)
    {
        string? relativePath = GetProjectRelativePath(file.AbsolutePath);
        if (string.IsNullOrEmpty(relativePath)) return;

        lock (_pluginItemLock)
        {
            if (!_pluginItems.TryGetValue(relativePath, out CodeEditor2.Data.TextFile? item)) return;
            _pluginItems.Remove(relativePath);
            item.Parent?.Items.TryRemove(item.Name);
        }
    }

    public ISystemVerilogDocument? GetDocument(ISystemVerilogFile file)
    {
        if (file is InMemoryFile mem)
        {
            return mem.GetOrCreateDocument();
        }
        return null;
    }

    public ISystemVerilogNamedElement? FindDefinition(ISystemVerilogFile file, int index)
    {
        if (file is not InMemoryFile mem) return null;
        // manually registered symbols take precedence (test / host use)
        ISystemVerilogNamedElement? manual = mem.FindManualElementAt(index);
        if (manual != null) return manual;
        InMemoryElement? at = mem.FindElementAt(index);
        if (at == null) return null;
        if (!at.IsReference) return at;
        return at.ResolveDeclaration() ?? at;
    }

    public IReadOnlyList<ISystemVerilogNamedElement> FindReferences(ISystemVerilogFile file, int index)
    {
        if (file is not InMemoryFile mem) return System.Array.Empty<ISystemVerilogNamedElement>();
        // manually registered symbols: return the symbol itself only
        ISystemVerilogNamedElement? manual = mem.FindManualElementAt(index);
        if (manual != null) return new[] { manual };
        InMemoryElement? at = mem.FindElementAt(index);
        if (at == null) return System.Array.Empty<ISystemVerilogNamedElement>();

        string name = at.Name;
        List<ISystemVerilogNamedElement> result = new();

        // include the declaration first (project-wide, this-file first)
        ISystemVerilogNamedElement? decl = at.IsReference ? at.ResolveDeclaration() : at;
        var seen = new HashSet<(string, int, int)>();
        if (decl is InMemoryElement d && d.DefinitionRange is { } dr && d.File is InMemoryFile df)
        {
            result.Add(new InMemoryElement(d.Name, d.Kind, dr, df, null));
            seen.Add((df.Id, dr.StartIndex, dr.EndIndex));
        }

        // all same-name elements across the project (skip the already-added declaration)
        foreach (InMemoryFile f in _files.Values)
        {
            foreach (InMemoryElement e in f.GetElementsByName(name))
            {
                if (e.DefinitionRange is { } r && seen.Add((f.Id, r.StartIndex, r.EndIndex)))
                {
                    result.Add(new InMemoryElement(e.Name, e.Kind, r, f, null));
                }
            }
        }
        return result;
    }

    public Task<ISystemVerilogDocument?> GetDocumentAsync(ISystemVerilogFile file, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetDocument(file));
    }

    public Task<ISystemVerilogNamedElement?> FindDefinitionAsync(ISystemVerilogFile file, int index, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(FindDefinition(file, index));
    }

    public Task<IReadOnlyList<ISystemVerilogNamedElement>> FindReferencesAsync(ISystemVerilogFile file, int index, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(FindReferences(file, index));
    }

    /// <summary>
    /// LSP textDocument/completion support: forwards to the file's
    /// CompletionContext based autocomplete entries.
    /// </summary>
    public IReadOnlyList<pluginVerilog.CoreBridge.CompletionAdapter.CompletionEntry>? GetCompletionItems(ISystemVerilogFile file, int index)
    {
        if (file is not InMemoryFile mem) return null;
        return mem.GetCompletionItems(index);
    }
}

internal sealed class InMemoryFile : ISystemVerilogFile
{
    private InMemoryDocument? _document;
    private readonly List<InMemoryElement> _allElements = new();
    private readonly Dictionary<string, List<InMemoryElement>> _byName = new();
    private readonly Dictionary<string, ISystemVerilogBuildingBlock> _buildingBlocks = new();
    private readonly List<ISystemVerilogNamedElement> _members = new();
    private readonly List<ISystemVerilogDiagnostic> _diagnostics = new();
    /// <summary>
    /// Guards <see cref="_diagnostics"/>. The list is written by the parse
    /// thread (SetDiagnostics) and read by the hierarchy listing (which runs on
    /// the LSP message loop), so an unguarded enumeration could throw
    /// "Collection was modified" and fail the whole rtl/hierarchy request.
    /// </summary>
    private readonly object _diagnosticsLock = new();
    /// <summary>Manually registered symbols (AddSymbol); take precedence over parser output.</summary>
    private readonly List<ISystemVerilogNamedElement> _manualList = new();
    private bool _manualSymbols;
    private bool _built;

    public InMemoryFile(string id, string? absolutePath, bool isSystemVerilog, string text)
    {
        Id = id;
        AbsolutePath = absolutePath;
        IsSystemVerilog = isSystemVerilog;
        _codeDocument = new InMemoryCodeDocument(text);
    }

    /// <summary>
    /// Lazily runs the lightweight parser on the document text. Skipped when
    /// symbols were registered manually (AddSymbol), so callers that build
    /// the symbol table themselves keep full control.
    /// </summary>
    private void EnsureBuilt()
    {
        // Serialise the build: a large workspace is parsed in the background at
        // initialize, and the hierarchy expand path may call EnsureBuilt on the
        // same file concurrently. Without the lock a second caller could return
        // before BuildSymbols finished (the _built flag is set first), leaving
        // the dependency unregistered and the Updater dropping the instance.
        lock (_buildLock)
        {
            if (_built || _manualSymbols) return;
            _built = true;
            BuildSymbols(((InMemoryCodeDocument)CodeDocument).GetText());
        }
    }

    private readonly object _buildLock = new();

    /// <summary>
    /// Forces the (lazy) symbol build immediately. Called by the LSP host on
    /// didOpen / didChange so queries against the freshly edited text always
    /// see real-parser results instead of a lightweight fallback on the very
    /// first query.
    /// </summary>
    internal void ForceBuild()
    {
        EnsureBuilt();
    }

    /// <summary>
    /// Parses this file if it has not been built yet. Used by the hierarchy
    /// expand path to make sure every instantiated module is registered in the
    /// project name spaces before the Updater walks the instance tree (an
    /// unparsed dependency is silently dropped by the Updater).
    /// </summary>
    internal void EnsureBuiltForResolve()
    {
        EnsureBuilt();
    }

    /// <summary>
    /// True once this file's symbols have been built (i.e. it was parsed at
    /// least once). Used by the workspace rescan to identify the files that
    /// still need a (re-)parse.
    /// </summary>
    internal bool IsBuilt => _built;

    /// <summary>
    /// Current text of the in-memory document.
    /// </summary>
    internal string CodeDocumentText => ((InMemoryCodeDocument)CodeDocument).GetText();

    /// <summary>
    /// Replaces the document text (used by didSave when the saved content
    /// differs from what didChange delivered, e.g. format-on-save).
    /// </summary>
    internal void SetText(string text)
    {
        _codeDocument = new InMemoryCodeDocument(text);
        _document = null;
        _verilogFile = null;
    }

    /// <summary>
    /// Re-runs the parse when the previous pass reported at least one error.
    /// Used by the workspace-wide second parse pass: a cross-file reference
    /// cannot resolve while the dependency is still unregistered, so the file
    /// that reported "unfound module" during the first pass is parsed again
    /// after every file has been registered (same idea as the editor's
    /// multi-pass ParseHierarchy).
    /// </summary>
    internal void RebuildIfHasErrors()
    {
        if (_manualSymbols) return;
        if (!_built) return;                 // never parsed: nothing to retry
        if (_parsedDocument == null) return;
        if (!HasErrors()) return;
        Rebuild();
    }

    private bool HasErrors()
    {
        lock (_diagnosticsLock)
        {
            foreach (ISystemVerilogDiagnostic d in _diagnostics)
            {
                if (d.Severity == SystemVerilogSeverity.Error) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Discards the current parse result and parses the current text again.
    /// </summary>
    internal void Rebuild()
    {
        if (_manualSymbols) return;
        // Same lock as EnsureBuilt: a background workspace parse and a didChange
        // rebuild must not interleave (one could clear the lists while the other
        // is still filling them).
        lock (_buildLock)
        {
            _allElements.Clear();
            _byName.Clear();
            _buildingBlocks.Clear();
            _members.Clear();
            lock (_diagnosticsLock) { _diagnostics.Clear(); }
            _parsedDocument = null;
            _document = null;
            _built = false;
            // drop the cached plugin file so the new parse uses a fresh document
            _verilogFile = null;
        }
        EnsureBuilt();
    }

    // ------------------------------------------------------------------
    // RTL hierarchy (NavigatePanel equivalent) for the VS Code tree view
    // ------------------------------------------------------------------


    /// <summary>
    /// Whether expanding this file would show any child node. Used for the
    /// expand arrow in the folder listing, which runs for every file in a
    /// directory: it must stay cheap, so it inspects the parse result directly
    /// instead of running the editor's Updater (which materialises the whole
    /// instance tree).
    /// </summary>
    internal bool HasHierarchyChildren()
    {
        EnsureBuilt();
        if (_parsedDocument?.Root == null) return false;

        foreach (pluginVerilog.Verilog.BuildingBlocks.BuildingBlock block
                 in _parsedDocument.Root.BuildingBlocks.Values)
        {
            if (block.GetBuildingBlockInstantiations().Count > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Children of the node addressed by <paramref name="nodePath"/> (an empty
    /// path means the file's first level), produced by the editor's own
    /// NavigatePanel update code
    /// (<see cref="pluginVerilog.Data.VerilogCommon.Updater"/>) rather than a
    /// re-implementation. The editor builds the tree from the plugin item's
    /// <c>Items</c> collection, so the LSP view and the editor's NavigateView
    /// stay identical by construction (same building-block rules, same
    /// instance-array handling, same include / imported-package children).
    ///
    /// <para>
    /// Like the editor, expansion is lazy: the Updater is run on the item being
    /// expanded (the file itself for the first level, the instance for a deeper
    /// level), which is exactly what the editor does when a node is expanded.
    /// Running it for the whole tree up front would parse every instantiated
    /// module eagerly.
    /// </para>
    ///
    /// <para>
    /// The plugin item is the one registered in the shared
    /// <see cref="InMemoryProject.PluginProject"/> item tree (see
    /// <see cref="InMemoryProject.RegisterPluginItem"/>); the Updater runs
    /// UI-free (it only touches <c>Data.Item.Items</c>), which the LSP host
    /// relies on.
    /// </para>
    /// </summary>
    internal List<HierarchyNodeDescriptor> GetHierarchyChildren(IReadOnlyList<string> instancePath)
    {
        EnsureBuilt();
        List<HierarchyNodeDescriptor> result = new();
        if (_parsedDocument?.Root == null) return result;

        pluginVerilog.Data.VerilogFile? pluginFile = GetPluginVerilogFile();
        if (pluginFile == null) return result;

        // Walk down to the item being expanded, running the editor's Updater on
        // each level so its Items collection is populated (the editor does the
        // same lazily when a node is expanded).
        //
        // The path is a chain of child NAMES, not indexes: an index would point
        // at a different node as soon as a sibling is added or removed between
        // two requests, which is exactly what the opaque expand token has to
        // survive.
        CodeEditor2.Data.Item current = pluginFile;
        List<string> parentSegments = new();
        foreach (string segment in instancePath)
        {
            List<CodeEditor2.Data.Item> children = GetUpdatedChildren(current);
            CodeEditor2.Data.Item? next = null;
            foreach (CodeEditor2.Data.Item candidate in children)
            {
                if (string.Equals(candidate.Name, segment, StringComparison.Ordinal))
                {
                    next = candidate;
                    break;
                }
            }
            if (next == null) return result;   // stale token: answer with nothing
            current = next;
            parentSegments.Add(next.Name);
        }

        // The children's instantiations are written in the document that was
        // parsed to produce them: the file itself for the first level, or the
        // instantiated module's source file for a deeper level (an instance
        // shares its source file's document). The expand token has to name
        // THAT document - it addresses the instance inside the file it is
        // written in, not the file it points at. Clicking, by contrast, opens
        // the child's own file (see CreateDescriptor), so the two are kept
        // apart: ExpandUri here, Uri there.
        string? documentUri = ProjectOwner?.GetUriForRelativePath(current.RelativePath);

        foreach (CodeEditor2.Data.Item child in GetUpdatedChildren(current))
        {
            HierarchyNodeDescriptor descriptor = CreateDescriptor(child, documentUri);
            descriptor.InstancePathSegments.AddRange(parentSegments);
            descriptor.InstancePathSegments.Add(child.Name);
            descriptor.InstancePath = string.Join(".", descriptor.InstancePathSegments);
            result.Add(descriptor);
        }
        return result;
    }

    /// <summary>
    /// Runs the editor's NavigatePanel update code on <paramref name="item"/>
    /// and returns its (now populated) children.
    ///
    /// <para>
    /// An instance (module / interface) is parsed on demand first, exactly like
    /// the editor: expanding a node parses the instantiated module (with the
    /// instance's parameter overrides) and then runs the Updater. Without the
    /// parse the instance has no <c>VerilogParsedDocument</c> and the Updater
    /// would leave <c>Items</c> empty.
    /// </para>
    ///
    /// <para>
    /// Everything here is UI-free: <c>CreateDocumentParser</c> /
    /// <c>ParseAsync</c> / <c>AcceptParsedDocumentAsync</c> only touch the
    /// plugin data model (the editor-side colour/refresh step is guarded by the
    /// headless checks in those methods).
    /// </para>
    /// </summary>
    /// <summary>
    /// Builds every workspace file that has not been parsed yet, so the editor's
    /// Updater can resolve the instantiations of the module being expanded.
    ///
    /// <para>
    /// The Updater silently drops an instantiation whose source module is not
    /// parsed yet (the DefinitionNameSpace has no entry for it), so expanding a
    /// file before its dependencies were parsed showed an empty level - which
    /// happens on a large workspace, where initialize parses in the background
    /// and the user expands a node before the background pass reaches the
    /// dependency. Building the remaining files here keeps the level complete.
    /// </para>
    ///
    /// <para>
    /// Cheap after the first expand: every file is flagged built, so subsequent
    /// calls are a no-op loop. The parse is synchronous and UI-free.
    /// </para>
    /// </summary>
    private void EnsureWorkspaceBuilt()
    {
        InMemoryProject? project = ProjectOwner;
        if (project == null) return;
        foreach (ISystemVerilogFile file in project.Files)
        {
            if (file is InMemoryFile mem) mem.EnsureBuiltForResolve();
        }
    }

    private List<CodeEditor2.Data.Item> GetUpdatedChildren(CodeEditor2.Data.Item item)
    {
        if (item is not pluginVerilog.Data.IVerilogRelatedFile verilogItem)
        {
            return item.Items.GetSnapShot();
        }

        try
        {
            EnsureWorkspaceBuilt();
            EnsureInstanceParsed(verilogItem);
            pluginVerilog.Data.VerilogCommon.Updater
                .UpdateAsync(verilogItem, _hierarchyUpdateSemaphore)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rtl/hierarchy: Updater failed for {item.Name}: {ex.Message}");
        }
        return item.Items.GetSnapShot();
    }

    /// <summary>
    /// Parses an instance whose <c>VerilogParsedDocument</c> is still missing
    /// (an instance is parsed lazily on expand, like the editor). The file root
    /// itself is already parsed by <see cref="EnsureBuilt"/>.
    /// </summary>
    private static void EnsureInstanceParsed(pluginVerilog.Data.IVerilogRelatedFile item)
    {
        // Re-parse when the parse result is stale: an instance caches the
        // ParsedDocument produced from its source file (keyed by module +
        // parameter overrides). When that source file is edited (didChange)
        // the instance's cached parse still reflects the old text, so the
        // hierarchy under a parent would keep showing the previous
        // instantiations. CheckDirty() compares the document version and sets
        // ReparseRequested, exactly like the editor.
        item.CheckDirty();
        if (item.VerilogParsedDocument != null && !item.ReparseRequested) return;

        CodeEditor2.CodeEditor.Parser.DocumentParser? parser = item.CreateDocumentParser(
            CodeEditor2.CodeEditor.Parser.DocumentParser.ParseModeEnum.BackgroundParse, null);
        if (parser == null) return;

        parser.ParseAsync().GetAwaiter().GetResult();
        item.AcceptParsedDocumentAsync(parser).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Serialises the editor's Updater runs. The Updater swaps
    /// <c>Data.Item.Items</c> under its own semaphore; sharing one instance
    /// across files keeps concurrent hierarchy requests from interleaving.
    /// </summary>
    private static readonly SemaphoreSlim _hierarchyUpdateSemaphore = new(1, 1);

    /// <summary>
    /// The plugin-side <see cref="pluginVerilog.Data.VerilogFile"/> for this
    /// in-memory file (the item the editor's NavigatePanel update code
    /// operates on). Null when the file is not registered in the plugin item
    /// tree (no workspace root yet).
    /// </summary>
    private pluginVerilog.Data.VerilogFile? GetPluginVerilogFile()
    {
        string? relativePath = ProjectOwner?.GetProjectRelativePath(AbsolutePath);
        if (string.IsNullOrEmpty(relativePath)) return null;
        return ProjectOwner?.GetPluginItem(relativePath) as pluginVerilog.Data.VerilogFile;
    }

    /// <summary>
    /// The plugin item for another file of the project, addressed by its
    /// document URI. Used to walk the instance tree of the file an
    /// instantiation is written in (which is not this file when the node being
    /// opened is the instantiated module).
    /// </summary>
    private pluginVerilog.Data.VerilogFile? GetPluginFileForUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        InMemoryProject? project = ProjectOwner;
        if (project == null) return null;
        if (project.FindFile(uri) is not InMemoryFile file) return null;
        string? relativePath = project.GetProjectRelativePath(file.AbsolutePath);
        if (string.IsNullOrEmpty(relativePath)) return null;
        return project.GetPluginItem(relativePath) as pluginVerilog.Data.VerilogFile;
    }

    /// <summary>
    /// Converts one NavigatePanel <see cref="CodeEditor2.Data.Item"/> (a
    /// module / interface instance, an imported package, an include header
    /// instance, ...) into a hierarchy node descriptor. Children are resolved
    /// lazily on expansion (see <see cref="GetHierarchyChildren"/>), so only
    /// the expandability is recorded here.
    /// </summary>
    private HierarchyNodeDescriptor CreateDescriptor(
        CodeEditor2.Data.Item item,
        string? documentUri)
    {
        HierarchyNodeDescriptor node = new()
        {
            Label = item.Name,
            Detail = DescribeItem(item),
            // Clicking the node opens the item's OWN file, exactly like the
            // editor's NavigatePanel: selecting a module instance calls
            // SetTextFileAsync(instance) and shows the instantiated module's
            // source, not the file the instantiation is written in. (Opening
            // the parent file here was the bug: clicking "AAA" under
            // PartialAssign.sv opened PartialAssign.sv instead of
            // moduleSample.v.)
            Uri = ResolveItemUri(item),
            // Expanding, on the other hand, has to look the instance up in the
            // file its instantiation is written in, so the expand token keeps
            // addressing that document.
            ExpandUri = documentUri ?? ResolveItemUri(item),
            // The client echoes this back in rtl/reparse so the server can walk
            // the parent's instance tree and parse the document AS this
            // instance (parameter overrides applied).
            ParentUri = documentUri,
            HasChildren = CanExpand(item),
            Icon = ResolveItemIcon(item),
            IconColor = ResolveItemIconColor(item),
        };

        // Click-to-jump position, resolved against the file the node OPENS (the
        // instantiated module's own source), so the caret lands on the module
        // declaration there. The instantiation statement's position belongs to
        // the parent document, which is no longer the file being opened.
        pluginVerilog.Verilog.Items.IBuildingBlockInstantiation? inst = GetInstantiatingItem(item);
        pluginVerilog.Verilog.BuildingBlocks.BuildingBlock? instanced = null;
        try
        {
            instanced = inst?.GetInstancedBuildingBlock();
        }
        catch (Exception)
        {
            // an unresolved cross-project instance may throw; the position is
            // cosmetic, so never fail the listing over it
        }
        if (instanced?.NameReference != null
            && instanced.File is CodeEditor2.Data.TextFile instancedFile)
        {
            CodeEditor2.CodeEditor.CodeDocument? instancedDocument = instancedFile.CodeDocument;
            node.StartLine = GetLineOfIndex(instancedDocument, instanced.NameReference.Index);
            node.StartCharacter = GetCharacterOfIndex(instancedDocument, instanced.NameReference.Index);
        }

        // absolute paths for the file operations (Add / Delete / Open in
        // Explorer). The item's RelativePath is workspace-relative, so resolve
        // it against the workspace roots here instead of in the client.
        string? fsPath = ResolveItemAbsolutePath(item);
        node.FsPath = fsPath;
        if (!string.IsNullOrEmpty(fsPath))
        {
            node.DirPath = Path.GetDirectoryName(fsPath);
        }
        return node;
    }

    /// <summary>
    /// Absolute path of the file behind a NavigatePanel item, or null when it
    /// cannot be resolved (no workspace root yet). Used for the file operations
    /// so the client never has to join paths itself.
    /// </summary>
    private string? ResolveItemAbsolutePath(CodeEditor2.Data.Item item)
    {
        string? relativePath = item.RelativePath;
        if (string.IsNullOrEmpty(relativePath)) return null;

        InMemoryProject? project = ProjectOwner;
        if (project == null) return null;

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (string root in project.WorkspaceRoots)
        {
            string candidate = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Whether expanding <paramref name="item"/> would show children, so the
    /// client can draw the expand arrow before the children are requested.
    ///
    /// <para>
    /// An instance is expandable when the module / interface it instantiates
    /// declares further instantiations. The instance's own parse result is not
    /// available yet (it is parsed lazily on expansion, like the editor), so
    /// the check resolves the instantiated building block from the parse of its
    /// source file — which is already loaded for the workspace.
    /// </para>
    /// </summary>
    private static bool CanExpand(CodeEditor2.Data.Item item)
    {
        // module / interface instance: look at the instantiated building block
        pluginVerilog.Verilog.Items.IBuildingBlockInstantiation? inst = GetInstantiatingItem(item);
        pluginVerilog.Verilog.BuildingBlocks.BuildingBlock? instanced = inst?.GetInstancedBuildingBlock();
        if (instanced != null)
        {
            return HasResolvableChildInstantiation(instanced);
        }

        // imported package / include header (or an instance whose source could
        // not be resolved): fall back to the item's own parse result
        if (item is not pluginVerilog.Data.IVerilogRelatedFile verilogItem) return false;
        pluginVerilog.Verilog.ParsedDocument? parsed = verilogItem.VerilogParsedDocument;
        if (parsed?.Root == null) return false;

        foreach (pluginVerilog.Verilog.BuildingBlocks.BuildingBlock block
                 in parsed.Root.BuildingBlocks.Values)
        {
            if (block.GetBuildingBlockInstantiations().Count > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// The instantiating item behind a NavigatePanel item, used for the
    /// click-to-jump position. Module / interface instances keep a weak
    /// reference to the instantiation they were created from.
    /// </summary>
    /// <summary>
    /// True when an instantiated building block declares at least one further
    /// instantiation that the editor's Updater will actually materialise (a
    /// module or interface instance whose source resolves). Only instantiations
    /// the Updater turns into child items count, so the expand arrow never
    /// appears for a node that would expand to nothing (e.g. an instance of an
    /// undefined module, which the Updater silently skips).
    /// </summary>
    private static bool HasResolvableChildInstantiation(
        pluginVerilog.Verilog.BuildingBlocks.BuildingBlock block)
    {
        foreach (pluginVerilog.Verilog.Items.IBuildingBlockInstantiation inst
                 in block.GetBuildingBlockInstantiations())
        {
            // The Updater only creates child items for module / interface
            // instantiations; program / udp / bind instantiations have no
            // navigate-node children.
            if (inst is not (pluginVerilog.Verilog.Items.ModuleInstantiation
                             or pluginVerilog.Verilog.Items.InterfaceInstance)) continue;

            if (inst.GetInstancedBuildingBlock() != null) return true;
        }
        return false;
    }

    private static pluginVerilog.Verilog.Items.IBuildingBlockInstantiation? GetInstantiatingItem(
        CodeEditor2.Data.Item item)
    {
        return item switch
        {
            pluginVerilog.Data.VerilogModuleInstance module => module.GetParentInstanciatiation(),
            pluginVerilog.Data.InterfaceInstance iface => iface.GetParentInstanciatiation(),
            _ => null,
        };
    }

    /// <summary>
    /// Short description shown next to the node label: the instantiated type
    /// for instances, the package name for imported packages, the file path for
    /// include headers.
    /// </summary>
    private static string? DescribeItem(CodeEditor2.Data.Item item)
    {
        return item switch
        {
            pluginVerilog.Data.VerilogModuleInstance module => module.InstanceName,
            pluginVerilog.Data.InterfaceInstance iface => iface.InstanceName,
            pluginVerilog.Data.ImportedPackage package => package.PackageName,
            pluginVerilog.Data.VerilogHeaderInstance header => header.RelativePath,
            _ => item.RelativePath,
        };
    }

    /// <summary>
    /// Icon name for a NavigatePanel item, decided from what the item
    /// represents: a module instance, an interface instance, an imported
    /// package or an include header.
    ///
    /// <para>
    /// The name is a stable token, not a VS Code codicon id: the client maps it
    /// onto the plugin's own SVG assets (shipped in the extension's icons/
    /// folder, light + dark variants) so the tree matches the desktop editor's
    /// NavigatePanel. Unknown names fall back to a codicon on the client.
    /// </para>
    /// </summary>
    private static string? ResolveItemIcon(CodeEditor2.Data.Item item)
    {
        switch (item)
        {
            case pluginVerilog.Data.VerilogModuleInstance module:
                // the instantiated building block decides the icon, so an
                // interface instantiated through the module path still shows
                // the interface icon. Resolving it can throw (the project
                // property is missing while a cross-project instance is still
                // unresolved), and an icon must never fail the tree listing.
                try
                {
                    return module.GetParentInstanciatiation()?.GetInstancedBuildingBlock()
                        is pluginVerilog.Verilog.BuildingBlocks.Interface
                        ? "interface"
                        : "module";
                }
                catch (Exception)
                {
                    return "module";
                }
            case pluginVerilog.Data.InterfaceInstance:
                return "interface";
            case pluginVerilog.Data.ImportedPackage:
                return "package";
            case pluginVerilog.Data.VerilogHeaderInstance:
                return "header";
            default:
                return "module";
        }
    }

    /// <summary>
    /// VS Code theme colour id used to flag a node whose file has errors or
    /// warnings, mirroring the editor's exclamation badge on NavigatePanel
    /// nodes. Null when the file is clean (or not parsed yet).
    /// </summary>
    private static string? ResolveItemIconColor(CodeEditor2.Data.Item item)
    {
        if (item is not pluginVerilog.Data.IVerilogRelatedFile file) return null;
        try
        {
            pluginVerilog.Verilog.ParsedDocument? parsed = file.VerilogParsedDocument;
            if (parsed == null) return null;
            if (parsed.ErrorCount > 0) return "errorForeground";
            if (parsed.WarningCount > 0) return "editorWarning.foreground";
        }
        catch (Exception)
        {
            // an unresolved instance may not have a parse result yet; the
            // colour is cosmetic, so never fail the listing over it
        }
        return null;
    }

    /// <summary>
    /// URI of the file the node OPENS when clicked: the item's own file. For a
    /// module / interface instance that is the instantiated module's source
    /// (the editor's NavigatePanel opens the instance's source file too), for
    /// an imported package / include header it is the package / header file.
    ///
    /// <para>
    /// This is deliberately NOT the file the instantiation is written in: that
    /// one is only used to build the expand token (see
    /// <see cref="HierarchyNodeDescriptor.ExpandUri"/>). Opening the parent
    /// file here was the bug where clicking an instance under PartialAssign.sv
    /// opened PartialAssign.sv instead of the instantiated moduleSample.v.
    /// </para>
    /// </summary>
    private string? ResolveItemUri(CodeEditor2.Data.Item item)
    {
        string? relativePath = item.RelativePath;
        string? uri = ProjectOwner?.GetUriForRelativePath(relativePath);
        // Fall back to this file's own URI when the item's file is not loaded
        // (an include that was never opened): better to open something than to
        // leave the click without a target.
        return uri ?? Id;
    }

    /// <summary>
    /// 0-based line of <paramref name="index"/> in <paramref name="doc"/>.
    ///
    /// <para>
    /// <c>CodeDocument.GetLineAt</c> returns AvaloniaEdit's 1-based
    /// <c>LineNumber</c>, while LSP positions are 0-based, so the value is
    /// converted here. Returning the raw 1-based number put every hierarchy
    /// node one line below its declaration.
    /// </para>
    /// </summary>
    private static int? GetLineOfIndex(CodeEditor2.CodeEditor.CodeDocument? doc, int index)
    {
        if (doc == null || index < 0 || index > doc.Length) return null;
        return doc.GetLineAt(index) - 1;
    }

    private static int? GetCharacterOfIndex(CodeEditor2.CodeEditor.CodeDocument? doc, int index)
    {
        if (doc == null || index < 0 || index > doc.Length) return null;
        // GetLineAt is 1-based (AvaloniaEdit LineNumber) and GetLineStartIndex
        // takes that same 1-based number, so the subtraction is consistent.
        int line = doc.GetLineAt(index);
        return index - doc.GetLineStartIndex(line);
    }



    /// <summary>
    /// LSP textDocument/completion support: runs the plugin's
    /// CompletionContext (partial parse driven autocomplete) on the current
    /// parse result at the given index and returns plain label/kind entries.
    /// Returns null when completion is unavailable (no real-parser result).
    /// </summary>
    internal IReadOnlyList<pluginVerilog.CoreBridge.CompletionAdapter.CompletionEntry>? GetCompletionItems(int index)
    {
        EnsureBuilt();
        if (_parsedDocument == null) return null;
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.CompletionAdapter.GetCompletionItems(verilogFile, _parsedDocument, index);
    }

    /// <summary>
    /// LSP textDocument/formatting support: the edits that reformat every
    /// module instantiation in the document (the same scope as the editor's
    /// "autoFormat" tool). Empty when there is nothing to format.
    /// </summary>
    internal IReadOnlyList<pluginVerilog.CoreBridge.FormattingAdapter.TextEditEntry> GetFormattingEdits()
    {
        EnsureBuilt();
        if (_parsedDocument == null) return Array.Empty<pluginVerilog.CoreBridge.FormattingAdapter.TextEditEntry>();
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.FormattingAdapter.GetFormattingEdits(verilogFile, _parsedDocument);
    }

    /// <summary>
    /// LSP textDocument/signatureHelp support: the argument hint the editor
    /// would pop up at the given index (the port / parameter label of the
    /// argument being typed), produced by the plugin's partial parse. Returns
    /// null when there is no hint at the caret.
    /// </summary>
    internal IReadOnlyList<pluginVerilog.CoreBridge.SignatureHelpAdapter.SignatureEntry>? GetSignatures(int index)
    {
        EnsureBuilt();
        if (_parsedDocument == null) return null;
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.SignatureHelpAdapter.GetSignatures(verilogFile, _parsedDocument, index);
    }

    /// <summary>
    /// LSP textDocument/foldingRange support: the fold regions the parser
    /// recorded on the document (begin/end, module/endmodule, ...). Returned as
    /// (startLine, endLine) pairs so the LSP layer does not need the editor's
    /// folding types. Empty when the file has not been parsed yet.
    /// </summary>
    internal IReadOnlyList<(int StartLine, int EndLine, string? Name)> GetFoldingRanges()
    {
        EnsureBuilt();
        List<(int, int, string?)> result = new();
        CodeEditor2.CodeEditor.CodeDocument? doc = _parsedDocument?.CodeDocument;
        if (doc == null) return result;

        foreach (AvaloniaEdit.Folding.NewFolding folding in doc.Foldings.Foldings)
        {
            int startLine = doc.GetLineAt(folding.StartOffset);
            // the fold ends on the line of the last character inside the region
            int endIndex = folding.EndOffset - 1;
            if (endIndex < folding.StartOffset) endIndex = folding.StartOffset;
            if (endIndex > doc.Length) endIndex = doc.Length;
            int endLine = doc.GetLineAt(endIndex);
            if (endLine <= startLine) continue;   // single-line region: nothing to fold
            result.Add((startLine, endLine, string.IsNullOrEmpty(folding.Name) ? null : folding.Name));
        }
        return result;
    }

    /// <summary>
    /// rtl/snippet support: the text the editor's snippet would insert at the
    /// given index (module instantiation, alwaysFF, modport, port inversion,
    /// port connection fill). The decision of <b>what</b> to insert lives in
    /// the plugin's UI-free <c>CoreBridge.SnippetAdapter</c>; the host only
    /// applies the returned text.
    /// </summary>
    internal pluginVerilog.CoreBridge.SnippetAdapter.SnippetResult? GetSnippets(
        int index,
        string name,
        string? argument,
        int selectionStart,
        int selectionEnd)
    {
        EnsureBuilt();
        if (_parsedDocument == null) return null;
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.SnippetAdapter.GetSnippets(
            verilogFile, _parsedDocument, index, name, argument, selectionStart, selectionEnd);
    }

    /// <summary>
    /// rtl/autoConnect support: the auto-connect form for the module
    /// instantiation at <paramref name="index"/> (one row per port with the
    /// signal the editor would connect). The decision of <b>which signal
    /// matches a port</b> lives in the plugin's UI-free
    /// <c>CoreBridge.AutoConnectAdapter</c>; the host only renders the rows.
    /// </summary>
    internal SystemVerilogCore.AutoConnect.AutoConnectModel? GetAutoConnectModel(int index)
    {
        EnsureBuilt();
        if (_parsedDocument == null) return null;
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.AutoConnectAdapter.Build(verilogFile, _parsedDocument, index);
    }

    /// <summary>
    /// rtl/autoConnectApply support: rebuilds the instantiation text with the
    /// chosen connections. Returns null when the instantiation cannot be
    /// rebuilt (the caller then reports "nothing to apply").
    /// </summary>
    internal string? ApplyAutoConnect(
        int index,
        IReadOnlyDictionary<string, string?> selections,
        string indent)
    {
        EnsureBuilt();
        if (_parsedDocument == null) return null;
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.AutoConnectAdapter.Apply(
            verilogFile, _parsedDocument, index, selections, indent);
    }

    /// <summary>
    /// Runs the real Verilog parser through the plugin's UI-free
    /// CoreBridge.ParseEngine and materialises declarations and references
    /// as InMemoryElements so definition / references / outline queries
    /// have real data to work with.
    /// </summary>
    private void BuildSymbols(string text)
    {
        // The real parser is async but free of UI dependencies; the query
        // path is synchronous, so block here (no dispatcher => no deadlock).
        pluginVerilog.Verilog.ParsedDocument? parsedDoc = ParseSystemVerilog(text).GetAwaiter().GetResult();
        if (parsedDoc == null || parsedDoc.Root == null)
        {
            // fall back to the lightweight parser when the real parser could
            // not run (e.g. file object not usable in this host)
            BuildSymbolsLightweight(text);
            return;
        }
        _parsedDocument = parsedDoc;
        BuildSymbolsFromParsedDocument(parsedDoc, text);
        // collect parse messages (errors / warnings / notices) as LSP
        // diagnostics; PushDiagnostics publishes them to the client
        SetDiagnostics(CollectDiagnostics(parsedDoc));
    }

    /// <summary>
    /// Converts the parser's message list (parse errors, warnings, notices,
    /// hints) into ISystemVerilogDiagnostic entries. The message Index/Length
    /// are document-relative so they map directly to LSP ranges.
    /// </summary>
    private static IEnumerable<ISystemVerilogDiagnostic> CollectDiagnostics(pluginVerilog.Verilog.ParsedDocument parsedDoc)
    {
        List<ISystemVerilogDiagnostic> result = new();
        foreach (CodeEditor2.CodeEditor.ParsedDocument.Message message in parsedDoc.Messages)
        {
            pluginVerilog.Verilog.ParsedDocument.Message? verilogMessage =
                message as pluginVerilog.Verilog.ParsedDocument.Message;
            SystemVerilogSeverity severity = verilogMessage?.Type switch
            {
                pluginVerilog.Verilog.ParsedDocument.Message.MessageType.Error => SystemVerilogSeverity.Error,
                pluginVerilog.Verilog.ParsedDocument.Message.MessageType.Warning => SystemVerilogSeverity.Warning,
                pluginVerilog.Verilog.ParsedDocument.Message.MessageType.Notice => SystemVerilogSeverity.Information,
                pluginVerilog.Verilog.ParsedDocument.Message.MessageType.Hint => SystemVerilogSeverity.Hint,
                _ => SystemVerilogSeverity.Error,
            };
            string code = pluginVerilog.CoreBridge.DiagnosticCodeMap.FromMessage(message.Text);
            int start = message.Index;
            if (start < 0) continue; // skip messages without a document position

            // The plugin's WordPointer length is the distance to the next
            // token (an editor mark convention), not the word length. For
            // LSP underlines the end must be the word end: scan the
            // identifier at the message position.
            CodeEditor2.CodeEditor.CodeDocument? doc = parsedDoc.CodeDocument;
            int length = 1;
            if (doc != null && start < doc.Length)
            {
                length = 0;
                int i = start;
                while (i < doc.Length && length < 256)
                {
                    char c = doc.GetCharAt(i);
                    if (char.IsLetterOrDigit(c) || c == '_' || c == '$') { length++; i++; }
                    else break;
                }
                if (length == 0)
                {
                    // non-identifier position (e.g. a punctuation token):
                    // consume the token itself up to a delimiter
                    while (i < doc.Length && length < 256)
                    {
                        char c = doc.GetCharAt(i);
                        if (char.IsWhiteSpace(c) || c == '\r' || c == '\n') break;
                        length++; i++;
                    }
                    if (length == 0) length = 1;
                }
            }
            result.Add(new InMemoryDiagnostic(
                severity,
                code,
                message.Text,
                new SystemVerilogRange(start, length)));
        }
        return result;
    }

    private sealed class InMemoryDiagnostic : ISystemVerilogDiagnostic
    {
        public InMemoryDiagnostic(SystemVerilogSeverity severity, string code, string message, SystemVerilogRange range)
        {
            Severity = severity;
            Code = code;
            Message = message;
            Range = range;
        }
        public SystemVerilogSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public SystemVerilogRange Range { get; }
    }

    private pluginVerilog.Verilog.ParsedDocument? _parsedDocument;

    /// <summary>
    /// The real parser's result for this file (null until it has been parsed).
    /// Exposed so the LSP handlers can walk the parse tree (e.g. the
    /// auto-connect form needs the instantiation's source range).
    /// </summary>
    internal pluginVerilog.Verilog.ParsedDocument? ParsedDocument
    {
        get
        {
            EnsureBuilt();
            return _parsedDocument;
        }
    }

    /// <summary>
    /// Drives the plugin ParseEngine on an in-memory VerilogFile.
    /// </summary>
    private System.Threading.Tasks.Task<pluginVerilog.Verilog.ParsedDocument?> ParseSystemVerilog(string text)
    {
        // create / reuse a plugin-side VerilogFile for this in-memory file
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.ParseEngine.ParseSystemVerilogAsync(text, verilogFile);
    }

    /// <summary>
    /// Re-parses this file AS the instance addressed by
    /// <paramref name="instancePath"/> (a chain of child names from the file
    /// root), so the parameter overrides written at the instantiation are
    /// applied to the parse result.
    ///
    /// <para>
    /// The editor opens the INSTANCE, not the file: selecting a module instance
    /// calls <c>SetTextFileAsync(instance)</c>, and the instance's parser is
    /// created with the instantiation's parameter overrides
    /// (<c>VerilogModuleInstance.CreateDocumentParser</c>). A plain file parse
    /// uses the declared defaults, so a module instantiated with
    /// <c>#(.P_WIDTH(5))</c> would still be shown with <c>P_WIDTH = 8</c> and
    /// every width derived from it would be wrong. This method reproduces the
    /// editor's behaviour for the LSP host.
    /// </para>
    ///
    /// <para>
    /// Returns false when the path does not resolve to an instance (a plain
    /// file, a stale token, or an instance whose source could not be resolved);
    /// the caller then keeps the plain file parse.
    /// </para>
    /// </summary>
    internal bool RebuildAsInstance(string? parentUri, IReadOnlyList<string> instancePath)
    {
        if (instancePath.Count == 0) return false;
        if (_manualSymbols) return false;

        // The instance path is relative to the file the instantiation is
        // WRITTEN IN (the parent), not to this file: clicking "u_child" under
        // top.sv opens child.sv, but the instance lives in top.sv. Walk the
        // parent's item tree to find it, then parse THIS file with the
        // instance's parameter overrides.
        pluginVerilog.Data.VerilogFile? pluginFile = GetPluginFileForUri(parentUri) ?? GetPluginVerilogFile();
        if (pluginFile == null) return false;

        // Walk the instance path with the editor's Updater so each level's
        // Items collection is populated (same as GetHierarchyChildren).
        CodeEditor2.Data.Item current = pluginFile;
        foreach (string segment in instancePath)
        {
            CodeEditor2.Data.Item? next = null;
            foreach (CodeEditor2.Data.Item candidate in GetUpdatedChildren(current))
            {
                if (string.Equals(candidate.Name, segment, StringComparison.Ordinal))
                {
                    next = candidate;
                    break;
                }
            }
            if (next == null) return false;
            current = next;
        }

        // The target must be an instance (module / interface): only those carry
        // parameter overrides and a target building-block name.
        string? moduleName;
        Dictionary<string, pluginVerilog.Verilog.Expressions.Expression> overrides;
        switch (current)
        {
            case pluginVerilog.Data.VerilogModuleInstance module:
                moduleName = module.InstanceName;
                overrides = module.ParameterOverrides;
                break;
            case pluginVerilog.Data.InterfaceInstance iface:
                moduleName = iface.InstanceName;
                overrides = iface.ParameterOverrides;
                break;
            default:
                return false;
        }
        if (string.IsNullOrEmpty(moduleName)) return false;

        pluginVerilog.Verilog.ParsedDocument? parsed;
        try
        {
            parsed = pluginVerilog.CoreBridge.ParseEngine.ParseInstanceAsync(
                CodeDocumentText, pluginFile, moduleName, overrides).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rtl/reparse: instance parse failed for {moduleName}: {ex.Message}");
            return false;
        }
        if (parsed?.Root == null) return false;

        lock (_buildLock)
        {
            _allElements.Clear();
            _byName.Clear();
            _buildingBlocks.Clear();
            _members.Clear();
            lock (_diagnosticsLock) { _diagnostics.Clear(); }
            _parsedDocument = parsed;
            _document = null;
            _built = true;
            _cachedTokens = null;
        }
        BuildSymbolsFromParsedDocument(parsed, CodeDocumentText);
        SetDiagnostics(CollectDiagnostics(parsed));
        return true;
    }

    private pluginVerilog.Data.VerilogFile? _verilogFile;

    private pluginVerilog.Data.VerilogFile CreateVerilogFile()
    {
        // Share the single plugin-side Project owned by the InMemoryProject.
        // A per-file Project would give every file its own ProjectProperty and
        // therefore its own DefinitionNameSpace / PackageNameSpace /
        // UnitNameSpace, so modules declared in other files of the same
        // workspace would never resolve ("unfound module").
        CodeEditor2.Data.Project project = ProjectOwner?.PluginProject
            ?? throw new InvalidOperationException(
                "InMemoryFile must be created through InMemoryProject.AddOrUpdateFile");

        // Prefer the plugin item registered in the shared item tree: the
        // editor's NavigatePanel update code (VerilogCommon.Updater) reads the
        // parse result from *that* item's ParsedDocument, so parsing a
        // throw-away VerilogFile would leave the tree empty. Reusing the
        // registered item keeps the parse result and the hierarchy in sync.
        if (GetPluginVerilogFile() is pluginVerilog.Data.VerilogFile registered)
        {
            return registered;
        }

        // RelativePath must be the workspace-relative path, not the document
        // URI: WordScanner.parseInclude derives the include target's
        // same-folder path from the including file's RelativePath and compares
        // it against the plugin item tree's RelativePath values. With a URI
        // ("file:///d%3A/...") the same-folder lookup never matched and only
        // the project-wide name fallback could resolve an include (which picks
        // the wrong file when two folders hold the same header name).
        string relativePath = ProjectOwner?.GetProjectRelativePath(AbsolutePath) ?? Id;

        pluginVerilog.Data.VerilogFile file = new pluginVerilog.Data.VerilogFile()
        {
            Name = System.IO.Path.GetFileName(relativePath),
            Project = project,
            RelativePath = relativePath,
        };
        file.SystemVerilog = IsSystemVerilog;
        return file;
    }

    /// <summary>
    /// Builds symbols from the real parser's ParsedDocument: building blocks
    /// (module/interface/...) become InMemoryBlocks, data objects / functions
    /// / tasks become declaration elements, WordReferences become references.
    /// </summary>
    private void BuildSymbolsFromParsedDocument(pluginVerilog.Verilog.ParsedDocument parsedDoc, string text)
    {
        var blockByBuildingBlock = new Dictionary<pluginVerilog.Verilog.BuildingBlocks.BuildingBlock, InMemoryBlock>();

        void AppendBuildingBlocks(pluginVerilog.Verilog.BuildingBlocks.BuildingBlock bb, InMemoryBlock? parent)
        {
            if (bb == null || string.IsNullOrEmpty(bb.Name)) return;

            SystemVerilogBuildingBlockKind kind = MapBuildingBlockKind(bb);
            var block = new InMemoryBlock(bb.Name, kind, this);
            blockByBuildingBlock[bb] = block;

            if (parent != null) parent.AddNestedBlock(block);
            else _buildingBlocks[block.Name] = block;

            // declaration element for the block itself
            int nameStart = FindNameStart(text, bb.Name);
            if (nameStart >= 0)
            {
                var declElement = new InMemoryElement(bb.Name, MapNamedElementType(bb),
                    new SystemVerilogRange(nameStart, bb.Name.Length), this, parent);
                block.SelfElement = declElement;
                RegisterElement(declElement);
            }

            // members declared inside the block
            foreach (pluginVerilog.Verilog.INamedElement element in bb.NamedElements)
            {
                if (element == null || string.IsNullOrEmpty(element.Name)) continue;

                // nested building blocks are appended recursively (below)
                if (element is pluginVerilog.Verilog.BuildingBlocks.BuildingBlock nested)
                {
                    AppendBuildingBlocks(nested, block);
                    continue;
                }

                int start = FindNameStart(text, element.Name);
                if (start < 0) continue;
                var memberElement = new InMemoryElement(element.Name, MapNamedElementType(element),
                    new SystemVerilogRange(start, element.Name.Length), this, block);
                RegisterElement(memberElement);
                block.AddMember(memberElement);
            }
        }

        if (parsedDoc.Root is pluginVerilog.Verilog.BuildingBlocks.BuildingBlock rootBlock)
        {
            // root is a synthetic block; append it without registering itself
            foreach (pluginVerilog.Verilog.INamedElement element in rootBlock.NamedElements)
            {
                if (element is pluginVerilog.Verilog.BuildingBlocks.BuildingBlock nested)
                {
                    AppendBuildingBlocks(nested, null);
                }
            }
        }

        // collect references (use / assign sites) reported by the real parser
        AppendReferences(parsedDoc.Root, new HashSet<pluginVerilog.Verilog.DataObjects.DataObject>());
    }

    /// <summary>
    /// Walks the parsed building-block tree and registers every reference
    /// site reported by the real parser (DataObject.DefinedReference /
    /// UsedReferences / AssignedReferences) as InMemoryElements, so the LSP
    /// references / definition queries work on real parser data.
    /// </summary>
    private void AppendReferences(pluginVerilog.Verilog.BuildingBlocks.BuildingBlock? block, HashSet<pluginVerilog.Verilog.DataObjects.DataObject> visited)
    {
        if (block == null) return;
        foreach (pluginVerilog.Verilog.INamedElement element in block.NamedElements)
        {
            if (element is pluginVerilog.Verilog.BuildingBlocks.BuildingBlock nested)
            {
                AppendReferences(nested, visited);
                continue;
            }
            if (element is pluginVerilog.Verilog.DataObjects.DataObject dataObject)
            {
                AppendReferences(dataObject, visited);
            }
        }
    }

    private void AppendReferences(pluginVerilog.Verilog.DataObjects.DataObject dataObject, HashSet<pluginVerilog.Verilog.DataObjects.DataObject> visited)
    {
        if (dataObject == null || !visited.Add(dataObject)) return;

        // declaration site
        if (dataObject.DefinedReference is { } definedRef && definedRef.Length > 0)
        {
            RegisterReference(dataObject.Name, definedRef.Index, definedRef.Length, isDeclaration: true);
        }

        // use sites
        AppendReferenceList(dataObject.Name, dataObject.UsedReferences);
        AppendReferenceList(dataObject.Name, dataObject.AssignedReferences);

        // members of struct / class objects carry their own references
        if (dataObject.NamedElements != null)
        {
            foreach (pluginVerilog.Verilog.INamedElement member in dataObject.NamedElements)
            {
                if (member is pluginVerilog.Verilog.DataObjects.DataObject memberDataObject)
                {
                    AppendReferences(memberDataObject, visited);
                }
            }
        }
    }

    private void AppendReferenceList(string name, List<pluginVerilog.Verilog.WordReference>? references)
    {
        if (references == null) return;
        foreach (pluginVerilog.Verilog.WordReference reference in references)
        {
            if (reference == null || reference.Length <= 0) continue;
            RegisterReference(name, reference.Index, reference.Length, isDeclaration: false);
        }
    }

    private void RegisterReference(string name, int index, int length, bool isDeclaration)
    {
        // The plugin's WordReference.Length is the distance to the *next* token
        // (an editor mark convention that also covers the whitespace and
        // comments in between), not the token width. Using it directly made
        // every LSP range (documentHighlight / references / rename) span from
        // the identifier to the following token, so clicking a symbol
        // highlighted far more than the word. Re-derive the width with the
        // plugin's own tokenizer, which is what the editor's
        // CodeDocument.GetWord returns for the same position.
        int tokenLength = GetTokenLength(index, length);
        var element = new InMemoryElement(name, SystemVerilogNamedElementKind.Unknown,
            new SystemVerilogRange(index, tokenLength), this, null, isReference: !isDeclaration);
        _allElements.Add(element);
        if (!_byName.TryGetValue(name, out var list))
        {
            list = new List<InMemoryElement>();
            _byName[name] = list;
        }
        list.Add(element);
    }

    /// <summary>
    /// Length of the token that starts at <paramref name="index"/>, using the
    /// plugin's own tokenizer so the range matches what the editor's
    /// <c>CodeDocument.GetWord</c> returns for the same position (identifiers,
    /// sized literals like <c>1'b0</c>, multi-character operators, ...).
    /// <paramref name="fallbackLength"/> is only used when the tokenizer cannot
    /// run (no parse result yet).
    /// </summary>
    private int GetTokenLength(int index, int fallbackLength)
    {
        CodeEditor2.CodeEditor.CodeDocument? doc = _parsedDocument?.CodeDocument;
        if (doc != null && index >= 0 && index < doc.Length)
        {
            int tokenIndex = index;
            string sectionName = "";
            pluginVerilog.Verilog.WordPointer.FetchNext(
                doc, ref tokenIndex, out int length, out _, out _, ref sectionName);
            // FetchNext skips leading blanks; the caller always passes a token
            // start, so tokenIndex stays put and length is the token width.
            if (length > 0 && tokenIndex == index) return length;
        }
        return fallbackLength > 0 ? fallbackLength : 1;
    }

    private void RegisterElement(InMemoryElement element)
    {
        _allElements.Add(element);
        if (!_byName.TryGetValue(element.Name, out var list))
        {
            list = new List<InMemoryElement>();
            _byName[element.Name] = list;
        }
        list.Add(element);
    }

    private static SystemVerilogNamedElementKind MapBuildingBlockKindToNamed(SystemVerilogBuildingBlockKind kind) => kind switch
    {
        SystemVerilogBuildingBlockKind.Module => SystemVerilogNamedElementKind.Module,
        SystemVerilogBuildingBlockKind.Interface => SystemVerilogNamedElementKind.Interface,
        SystemVerilogBuildingBlockKind.Package => SystemVerilogNamedElementKind.Package,
        SystemVerilogBuildingBlockKind.Program => SystemVerilogNamedElementKind.Program,
        SystemVerilogBuildingBlockKind.Checker => SystemVerilogNamedElementKind.Checker,
        SystemVerilogBuildingBlockKind.Primitive => SystemVerilogNamedElementKind.Primitive,
        SystemVerilogBuildingBlockKind.Class => SystemVerilogNamedElementKind.Class,
        _ => SystemVerilogNamedElementKind.Unknown,
    };

    private static SystemVerilogNamedElementKind MapNamedElementType(pluginVerilog.Verilog.INamedElement element)
    {
        if (element is pluginVerilog.Verilog.DataObjects.Nets.Net) return SystemVerilogNamedElementKind.Net;
        if (element is pluginVerilog.Verilog.DataObjects.Port) return SystemVerilogNamedElementKind.Port;
        if (element is pluginVerilog.Verilog.DataObjects.Constants.Parameter) return SystemVerilogNamedElementKind.Parameter;
        if (element is pluginVerilog.Verilog.DataObjects.Constants.Localparam) return SystemVerilogNamedElementKind.LocalParameter;
        if (element is pluginVerilog.Verilog.DataObjects.Typedef) return SystemVerilogNamedElementKind.Typedef;
        if (element is pluginVerilog.Verilog.Function) return SystemVerilogNamedElementKind.Function;
        if (element is pluginVerilog.Verilog.Task_) return SystemVerilogNamedElementKind.Task;
        if (element is pluginVerilog.Verilog.BuildingBlocks.Module) return SystemVerilogNamedElementKind.Module;
        if (element is pluginVerilog.Verilog.BuildingBlocks.Interface) return SystemVerilogNamedElementKind.Interface;
        if (element is pluginVerilog.Verilog.BuildingBlocks.Package) return SystemVerilogNamedElementKind.Package;
        if (element is pluginVerilog.Verilog.BuildingBlocks.Class) return SystemVerilogNamedElementKind.Class;
        return SystemVerilogNamedElementKind.Variable;
    }

    private static SystemVerilogBuildingBlockKind MapBuildingBlockKind(pluginVerilog.Verilog.BuildingBlocks.BuildingBlock bb) => bb switch
    {
        pluginVerilog.Verilog.BuildingBlocks.Module => SystemVerilogBuildingBlockKind.Module,
        pluginVerilog.Verilog.BuildingBlocks.Interface => SystemVerilogBuildingBlockKind.Interface,
        pluginVerilog.Verilog.BuildingBlocks.Package => SystemVerilogBuildingBlockKind.Package,
        pluginVerilog.Verilog.BuildingBlocks.Program => SystemVerilogBuildingBlockKind.Program,
        pluginVerilog.Verilog.BuildingBlocks.Checker => SystemVerilogBuildingBlockKind.Checker,
        pluginVerilog.Verilog.BuildingBlocks.Primitive => SystemVerilogBuildingBlockKind.Primitive,
        pluginVerilog.Verilog.BuildingBlocks.Class => SystemVerilogBuildingBlockKind.Class,
        _ => SystemVerilogBuildingBlockKind.Unknown,
    };

    /// <summary>
    /// Finds the start index of the given identifier in the text (first
    /// whole-word occurrence after line starts). Good enough for range
    /// anchoring; the real parser does not expose declaration offsets here.
    /// </summary>
    private static int FindNameStart(string text, string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        int searchFrom = 0;
        while (searchFrom <= text.Length - name.Length)
        {
            int idx = text.IndexOf(name, searchFrom, StringComparison.Ordinal);
            if (idx < 0) return -1;
            bool wordStart = idx == 0 || !(char.IsLetterOrDigit(text[idx - 1]) || text[idx - 1] == '_' || text[idx - 1] == '$');
            int end = idx + name.Length;
            bool wordEnd = end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '$');
            if (wordStart && wordEnd) return idx;
            searchFrom = idx + 1;
        }
        return -1;
    }

    /// <summary>
    /// Original lightweight-parser based symbol build; kept as fallback.
    /// </summary>
    private void BuildSymbolsLightweight(string text)
    {
        ParseResult parsed = LightweightParser.Parse(text);

        // Create an InMemoryBlock per scope, keyed by the Scope instance.
        var blockByScope = new Dictionary<Scope, InMemoryBlock>();
        foreach (Scope scope in parsed.Scopes)
        {
            var block = new InMemoryBlock(scope.Name, KindToBuildingBlockKind(scope.Kind), this);
            blockByScope[scope] = block;
        }

        foreach (SymbolInfo sym in parsed.Symbols)
        {
            InMemoryBlock? owner = (sym.Owner != null && blockByScope.TryGetValue(sym.Owner, out var b))
                ? b : null;
            // SymbolInfo.End is an exclusive end index; SystemVerilogRange
            // takes a length.
            var element = new InMemoryElement(sym.Name, KindToNamedElementKind(sym.Kind),
                new SystemVerilogRange(sym.Start, sym.End - sym.Start), this, owner);
            _allElements.Add(element);
            if (!_byName.TryGetValue(sym.Name, out var list))
            {
                list = new List<InMemoryElement>();
                _byName[sym.Name] = list;
            }
            list.Add(element);

            if (owner != null)
            {
                owner.AddMember(element);
            }
            else
            {
                _members.Add(element);
            }
        }

        // Register blocks: top-level ones in the file table, nested ones on
        // their owner block.
        foreach (var pair in blockByScope)
        {
            Scope s = pair.Key;
            InMemoryBlock b = pair.Value;
            // attach the declaration element (same name, same range) to the block
            foreach (InMemoryElement e in _allElements)
            {
                if (e.Name == s.Name && e.DefinitionRange is { } r &&
                    r.StartIndex == s.NameStart)
                {
                    b.SelfElement = e;
                    break;
                }
            }
            if (s.Owner != null && blockByScope.TryGetValue(s.Owner, out var parent))
            {
                parent.AddNestedBlock(b);
            }
            else
            {
                _buildingBlocks[b.Name] = b;
            }
        }

        foreach (ReferenceInfo rf in parsed.References)
        {
            // ReferenceInfo.End is an exclusive end index; SystemVerilogRange
            // takes a length.
            var element = new InMemoryElement(rf.Name, SystemVerilogNamedElementKind.Unknown,
                new SystemVerilogRange(rf.Start, rf.End - rf.Start), this, null, isReference: true);
            _allElements.Add(element);
            if (!_byName.TryGetValue(rf.Name, out var list))
            {
                list = new List<InMemoryElement>();
                _byName[rf.Name] = list;
            }
            list.Add(element);
        }
    }

    private static SystemVerilogNamedElementKind KindToNamedElementKind(SystemVerilogNamedElementKindLocal kind) => kind switch
    {
        SystemVerilogNamedElementKindLocal.Module => SystemVerilogNamedElementKind.Module,
        SystemVerilogNamedElementKindLocal.Interface => SystemVerilogNamedElementKind.Interface,
        SystemVerilogNamedElementKindLocal.Package => SystemVerilogNamedElementKind.Package,
        SystemVerilogNamedElementKindLocal.Program => SystemVerilogNamedElementKind.Program,
        SystemVerilogNamedElementKindLocal.Checker => SystemVerilogNamedElementKind.Checker,
        SystemVerilogNamedElementKindLocal.Primitive => SystemVerilogNamedElementKind.Primitive,
        SystemVerilogNamedElementKindLocal.Class => SystemVerilogNamedElementKind.Class,
        SystemVerilogNamedElementKindLocal.Function => SystemVerilogNamedElementKind.Function,
        SystemVerilogNamedElementKindLocal.Task => SystemVerilogNamedElementKind.Task,
        SystemVerilogNamedElementKindLocal.Parameter => SystemVerilogNamedElementKind.Parameter,
        SystemVerilogNamedElementKindLocal.LocalParameter => SystemVerilogNamedElementKind.LocalParameter,
        SystemVerilogNamedElementKindLocal.Port => SystemVerilogNamedElementKind.Port,
        SystemVerilogNamedElementKindLocal.Net => SystemVerilogNamedElementKind.Net,
        SystemVerilogNamedElementKindLocal.Typedef => SystemVerilogNamedElementKind.Typedef,
        _ => SystemVerilogNamedElementKind.Variable,
    };

    private static SystemVerilogBuildingBlockKind KindToBuildingBlockKind(ScopeKind kind) => kind switch
    {
        ScopeKind.Module => SystemVerilogBuildingBlockKind.Module,
        ScopeKind.Interface => SystemVerilogBuildingBlockKind.Interface,
        ScopeKind.Package => SystemVerilogBuildingBlockKind.Package,
        ScopeKind.Program => SystemVerilogBuildingBlockKind.Program,
        ScopeKind.Checker => SystemVerilogBuildingBlockKind.Checker,
        ScopeKind.Primitive => SystemVerilogBuildingBlockKind.Primitive,
        ScopeKind.Class => SystemVerilogBuildingBlockKind.Class,
        _ => SystemVerilogBuildingBlockKind.Unknown,
    };

    public string Id { get; }
    public string? AbsolutePath { get; }
    public bool IsSystemVerilog { get; }
    private ISystemVerilogCodeDocument _codeDocument;
    public ISystemVerilogCodeDocument CodeDocument => _codeDocument;

    /// <summary>Project this file belongs to (set by the project on add).</summary>
    internal InMemoryProject? ProjectOwner;

    public IReadOnlyList<ISystemVerilogBuildingBlock> TopLevelBlocks =>
        new List<ISystemVerilogBuildingBlock>(_buildingBlocks.Values);

    public void AddSymbol(ISystemVerilogNamedElement symbol)
    {
        _manualSymbols = true;
        _manualList.Add(symbol);
        if (symbol is InMemoryElement e)
        {
            _allElements.Add(e);
            if (!_byName.TryGetValue(e.Name, out var list))
            {
                list = new List<InMemoryElement>();
                _byName[e.Name] = list;
            }
            list.Add(e);
        }
    }

    /// <summary>
    /// Registers a top-level building block so the documentSymbol
    /// provider can list it. Re-adding the same name replaces the
    /// previous block.
    /// </summary>
    public void AddBuildingBlock(ISystemVerilogBuildingBlock block) =>
        _buildingBlocks[block.Name] = block;

    /// <summary>
    /// Registers a directly-declared named element of the file (e.g. a
    /// package-level typedef or constant). These are exposed through the
    /// root's <c>Members</c> collection.
    /// </summary>
    public void AddMember(ISystemVerilogNamedElement member) => _members.Add(member);

    internal IReadOnlyList<ISystemVerilogNamedElement> Members => _members;

    public ISystemVerilogDocument GetOrCreateDocument() => _document ??= new InMemoryDocument(this);

    /// <summary>
    /// Finds the element whose range covers the index (declaration or
    /// reference). If the index is on a reference, the resolved declaration
    /// (same name, first declaration in any file) is returned.
    /// </summary>
    public ISystemVerilogNamedElement? FindDefinition(int index)
    {
        InMemoryElement? at = FindElementAt(index);
        if (at == null) return null;
        if (!at.IsReference) return at;
        return at.ResolveDeclaration() ?? (ISystemVerilogNamedElement?)at;
    }

    public InMemoryElement? FindElementAt(int index)
    {
        EnsureBuilt();
        foreach (InMemoryElement e in _allElements)
        {
            if (e.DefinitionRange is { } r && r.Contains(index)) return e;
        }
        return null;
    }

    /// <summary>
    /// Manual symbols (registered via AddSymbol) take precedence over
    /// parser-produced elements for the definition / references queries.
    /// </summary>
    /// <summary>Colouring tokens for the document (lazily parsed).</summary>
    internal List<TokenInfo> GetTokens()
    {
        EnsureBuilt();
        // Prefer tokens derived from the real parser's color segments when
        // available (editor-identical coloring); fall back to the lightweight
        // scanner for files the real parser could not handle.
        List<TokenInfo>? parserTokens = GetParserColorTokens();
        if (parserTokens != null && parserTokens.Count > 0)
        {
            parserTokens = NormalizeTokens(parserTokens);
            _cachedTokens = parserTokens;
            return parserTokens;
        }
        return _cachedTokens ??= NormalizeTokens(
            LightweightParser.Parse(((InMemoryCodeDocument)CodeDocument).GetText()).Tokens);
    }

    /// <summary>
    /// Make a token list a valid LSP semantic-token stream: strictly
    /// increasing start offsets, no duplicates and no overlaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real parser writes the same span more than once. A module instance
    /// port connection such as <c>.AA ( aaaa )</c> gets both the generic
    /// reference colour and the data-object colour for <c>AA</c> / <c>aaaa</c>
    /// (observed on <c>verilog_sample/mySample/mod2.v</c>: <c>AA</c> as
    /// Identifier + Net and <c>aaaa</c> as Net + Identifier at the exact same
    /// offset), and declarations are coloured by both the declaration path and
    /// the identifier path.
    /// </para>
    /// <para>
    /// Emitting them all breaks two rules VS Code enforces on semantic tokens:
    /// the stream must be sorted, and a token must not overlap the previous
    /// one. VS Code logs <c>Overlapping semantic tokens detected at
    /// lineNumber N, column M</c> and drops the offending token, so the span
    /// silently loses its colour. Folding the duplicates here keeps the most
    /// specific type (register / net / parameter ... over the generic
    /// "identifier") and makes the rest of the stream valid.
    /// </para>
    /// </remarks>
    internal static List<TokenInfo> NormalizeTokens(List<TokenInfo> tokens)
    {
        if (tokens.Count <= 1) return tokens;

        List<TokenInfo> sorted = new(tokens);
        sorted.Sort((a, b) =>
        {
            int byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0) return byStart;
            // same start: most specific type first, then the longest span
            int byPriority = TokenTypePriority(a.Type).CompareTo(TokenTypePriority(b.Type));
            if (byPriority != 0) return byPriority;
            return b.End.CompareTo(a.End);
        });

        List<TokenInfo> result = new(sorted.Count);
        int coveredEnd = -1;
        foreach (TokenInfo token in sorted)
        {
            if (token.End <= token.Start) continue;     // empty span
            if (token.Start < coveredEnd) continue;     // overlaps an emitted token
            result.Add(token);
            coveredEnd = token.End;
        }
        return result;
    }

    /// <summary>
    /// Ranking used by <see cref="NormalizeTokens"/> when two tokens start at
    /// the same offset: a smaller value wins. A concrete data type
    /// (register / net / parameter / variable) is more informative than the
    /// generic "identifier" the reference path emits.
    /// </summary>
    private static int TokenTypePriority(TokenTypes type) => type switch
    {
        TokenTypes.Register => 0,
        TokenTypes.Property => 1,   // net
        TokenTypes.Parameter => 2,
        TokenTypes.Variable => 3,
        TokenTypes.Type => 4,
        TokenTypes.Function => 5,
        TokenTypes.Macro => 6,
        TokenTypes.Keyword => 7,
        TokenTypes.Number => 8,
        TokenTypes.String => 9,
        TokenTypes.Comment => 10,
        _ => 11,                    // identifier (generic reference)
    };

    /// <summary>
    /// Builds tokens from the real parser's per-line color segments via the
    /// plugin's ColorSegmentAdapter. Returns null when no parsed document is
    /// available (e.g. the real parser was skipped).
    /// </summary>
    private List<TokenInfo>? GetParserColorTokens()
    {
        try
        {
            pluginVerilog.Verilog.ParsedDocument? parsed = _parsedDocument;
            if (parsed == null) return null;

            CodeEditor2.CodeEditor.CodeDrawStyle baseDrawStyle = new CodeEditor2.CodeEditor.CodeDrawStyle();
            List<pluginVerilog.CoreBridge.ColorSegmentAdapter.Segment> segments =
                pluginVerilog.CoreBridge.ColorSegmentAdapter.GetSegments(parsed);
            if (segments.Count == 0) return null;

            List<TokenInfo> tokens = new(segments.Count);
            foreach (var s in segments)
            {
                tokens.Add(new TokenInfo { Start = s.Index, End = s.Index + s.Length, Type = ColorTypeToTokenType(s.Type) });
            }
            return tokens;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the plugin CodeDrawStyle palette index to the LSP token type
    /// legend (keyword, comment, string, number, macro, function, type,
    /// variable, property, parameter, register, identifier).
    /// </summary>
    private static TokenTypes ColorTypeToTokenType(pluginVerilog.CodeDrawStyle.ColorType colorType) => colorType switch
    {
        pluginVerilog.CodeDrawStyle.ColorType.Keyword => TokenTypes.Keyword,
        pluginVerilog.CodeDrawStyle.ColorType.Comment => TokenTypes.Comment,
        pluginVerilog.CodeDrawStyle.ColorType.HighLightedComment => TokenTypes.Comment,
        pluginVerilog.CodeDrawStyle.ColorType.CommentAnnotation => TokenTypes.Comment,
        pluginVerilog.CodeDrawStyle.ColorType.Number => TokenTypes.Number,
        pluginVerilog.CodeDrawStyle.ColorType.Identifier => TokenTypes.Identifier,
        pluginVerilog.CodeDrawStyle.ColorType.Register => TokenTypes.Register,
        pluginVerilog.CodeDrawStyle.ColorType.Net => TokenTypes.Property,
        pluginVerilog.CodeDrawStyle.ColorType.Variable => TokenTypes.Variable,
        pluginVerilog.CodeDrawStyle.ColorType.Parameter => TokenTypes.Parameter,
        _ => TokenTypes.Identifier,
    };

    private List<TokenInfo>? _cachedTokens;

    internal ISystemVerilogNamedElement? FindManualElementAt(int index)
    {
        foreach (ISystemVerilogNamedElement e in _manualList)
        {
            if (e.DefinitionRange is { } r && r.Contains(index)) return e;
        }
        return null;
    }

    /// <summary>All elements sharing a name (declarations + references), across the project.</summary>
    internal List<InMemoryElement> GetElementsByName(string name)
    {
        EnsureBuilt();
        return _byName.TryGetValue(name, out var list) ? list : new List<InMemoryElement>();
    }

    /// <summary>
    /// Every declaration element in this file (references excluded), for
    /// workspace/symbol. Empty until the file has been parsed.
    /// </summary>
    internal List<InMemoryElement> GetDeclarationElements()
    {
        EnsureBuilt();
        List<InMemoryElement> result = new();
        foreach (List<InMemoryElement> list in _byName.Values)
        {
            foreach (InMemoryElement e in list)
            {
                if (!e.IsReference) result.Add(e);
            }
        }
        return result;
    }

    /// <summary>First declaration element with the given name in this file (project fallback handled by the project).</summary>
    internal InMemoryElement? FindDeclarationByName(string name)
    {
        EnsureBuilt();
        if (_byName.TryGetValue(name, out var list))
        {
            foreach (InMemoryElement e in list)
            {
                if (!e.IsReference) return e;
            }
        }
        return null;
    }

    public IReadOnlyList<ISystemVerilogNamedElement> FindReferences(int index)
    {
        InMemoryElement? at = FindElementAt(index);
        if (at == null) return System.Array.Empty<ISystemVerilogNamedElement>();
        List<ISystemVerilogNamedElement> result = new();
        string name = at.Name;
        // same-name elements in this file
        if (_byName.TryGetValue(name, out var list))
        {
            foreach (InMemoryElement e in list) result.Add(e);
        }
        return result;
    }

    /// <summary>
    /// Snapshot of the current diagnostics. Returns a copy so callers can
    /// enumerate it while the parse thread replaces the list (the hierarchy
    /// listing reads this for every file in a directory).
    /// </summary>
    internal IReadOnlyList<ISystemVerilogDiagnostic> Diagnostics
    {
        get
        {
            lock (_diagnosticsLock)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    /// <summary>Replaces the diagnostic list after a (re)parse.</summary>
    internal void SetDiagnostics(IEnumerable<ISystemVerilogDiagnostic> diags)
    {
        lock (_diagnosticsLock)
        {
            _diagnostics.Clear();
            _diagnostics.AddRange(diags);
        }
    }

    /// <summary>
    /// Highest severity present in the current diagnostics, or null when the
    /// file is clean. Used by the hierarchy listing to colour the file icon
    /// without copying the whole diagnostic list.
    /// </summary>
    internal SystemVerilogSeverity? HighestDiagnosticSeverity()
    {
        lock (_diagnosticsLock)
        {
            SystemVerilogSeverity? highest = null;
            foreach (ISystemVerilogDiagnostic d in _diagnostics)
            {
                if (d.Severity == SystemVerilogSeverity.Error) return SystemVerilogSeverity.Error;
                if (d.Severity == SystemVerilogSeverity.Warning) highest = SystemVerilogSeverity.Warning;
                else if (highest == null) highest = d.Severity;
            }
            return highest;
        }
    }
}

/// <summary>A named element (declaration or reference) produced by the lightweight parser.</summary>
internal sealed class InMemoryElement : ISystemVerilogNamedElement
{
    private readonly InMemoryFile _file;
    private readonly InMemoryBlock? _ownerBlock;

    public InMemoryElement(string name, SystemVerilogNamedElementKind kind,
        SystemVerilogRange range, InMemoryFile file, InMemoryBlock? owner, bool isReference = false)
    {
        Name = name;
        Kind = kind;
        Range = range;
        _file = file;
        _ownerBlock = owner;
        IsReference = isReference;
    }

    public string Name { get; }
    public SystemVerilogNamedElementKind Kind { get; }
    public SystemVerilogRange Range { get; }
    public bool IsReference { get; }

    public SystemVerilogRange? DefinitionRange => Range;

    public ISystemVerilogBuildingBlock? Owner => _ownerBlock;

    public ISystemVerilogFile? File => _file;

    /// <summary>
    /// Resolves this reference to its declaration: first a declaration of
    /// the same name in the same file, otherwise a project-wide lookup.
    /// </summary>
    public ISystemVerilogNamedElement? ResolveDeclaration()
    {
        InMemoryElement? local = _file.FindDeclarationByName(Name);
        if (local != null) return local;
        return _file.ProjectOwner?.FindDeclarationProjectWide(Name);
    }
}

/// <summary>A building block (module / interface / package / class ...) extracted by the parser.</summary>
internal sealed class InMemoryBlock : ISystemVerilogBuildingBlock
{
    private readonly Dictionary<string, ISystemVerilogBuildingBlock> _nested = new();
    private readonly List<ISystemVerilogNamedElement> _members = new();

    public InMemoryBlock(string name, SystemVerilogBuildingBlockKind kind, InMemoryFile file)
    {
        Name = name;
        Kind = kind;
        File = file;
    }

    public string Name { get; }
    public SystemVerilogBuildingBlockKind Kind { get; }
    public ISystemVerilogFile? File { get; }

    /// <summary>Declaration element for this block (set after parsing).</summary>
    internal InMemoryElement? SelfElement;

    public SystemVerilogRange? DefinitionRange => SelfElement?.DefinitionRange;

    // ISystemVerilogBuildingBlock.Kind (new) shadows the inherited
    // ISystemVerilogNamedElement.Kind; expose the named-element kind
    // through an explicit interface implementation.
    SystemVerilogNamedElementKind ISystemVerilogNamedElement.Kind => Kind switch
    {
        SystemVerilogBuildingBlockKind.Module => SystemVerilogNamedElementKind.Module,
        SystemVerilogBuildingBlockKind.Interface => SystemVerilogNamedElementKind.Interface,
        SystemVerilogBuildingBlockKind.Package => SystemVerilogNamedElementKind.Package,
        SystemVerilogBuildingBlockKind.Program => SystemVerilogNamedElementKind.Program,
        SystemVerilogBuildingBlockKind.Checker => SystemVerilogNamedElementKind.Checker,
        SystemVerilogBuildingBlockKind.Primitive => SystemVerilogNamedElementKind.Primitive,
        SystemVerilogBuildingBlockKind.Class => SystemVerilogNamedElementKind.Class,
        _ => SystemVerilogNamedElementKind.Unknown,
    };

    public IReadOnlyDictionary<string, ISystemVerilogBuildingBlock> BuildingBlocks => _nested;

    public IReadOnlyList<ISystemVerilogNamedElement> Members => _members;

    internal void AddMember(ISystemVerilogNamedElement member) => _members.Add(member);

    internal void AddNestedBlock(InMemoryBlock block) => _nested[block.Name] = block;

    public IReadOnlyList<ISystemVerilogAutocompleteItem> AutocompleteItems
    {
        get
        {
            List<ISystemVerilogAutocompleteItem> items = new();
            foreach (ISystemVerilogNamedElement member in _members)
            {
                items.Add(new SystemVerilogAutocompleteItem(
                    member.Name,
                    member.Kind switch
                    {
                        SystemVerilogNamedElementKind.Module => SystemVerilogAutocompleteItemKind.Module,
                        SystemVerilogNamedElementKind.Interface => SystemVerilogAutocompleteItemKind.Interface,
                        SystemVerilogNamedElementKind.Package => SystemVerilogAutocompleteItemKind.Package,
                        SystemVerilogNamedElementKind.Program => SystemVerilogAutocompleteItemKind.Program,
                        SystemVerilogNamedElementKind.Checker => SystemVerilogAutocompleteItemKind.Checker,
                        SystemVerilogNamedElementKind.Primitive => SystemVerilogAutocompleteItemKind.Primitive,
                        SystemVerilogNamedElementKind.Class => SystemVerilogAutocompleteItemKind.Class,
                        SystemVerilogNamedElementKind.Function => SystemVerilogAutocompleteItemKind.Function,
                        SystemVerilogNamedElementKind.Task => SystemVerilogAutocompleteItemKind.Task,
                        SystemVerilogNamedElementKind.Typedef => SystemVerilogAutocompleteItemKind.Typedef,
                        SystemVerilogNamedElementKind.Net => SystemVerilogAutocompleteItemKind.Net,
                        SystemVerilogNamedElementKind.Port => SystemVerilogAutocompleteItemKind.Port,
                        SystemVerilogNamedElementKind.Parameter => SystemVerilogAutocompleteItemKind.Parameter,
                        SystemVerilogNamedElementKind.LocalParameter => SystemVerilogAutocompleteItemKind.LocalParameter,
                        _ => SystemVerilogAutocompleteItemKind.Variable,
                    }));
            }
            return items;
        }
    }

    public ISystemVerilogBuildingBlock? Owner => null;
}

internal sealed class InMemoryCodeDocument : ISystemVerilogCodeDocument
{
    private string _text;
    private int[] _lineStarts;

    public InMemoryCodeDocument(string text)
    {
        _text = text ?? string.Empty;
        _lineStarts = ComputeLineStarts(_text);
        Version = 1;
    }

    public int Length => _text.Length;
    public int LineCount => _lineStarts.Length;
    public ulong Version { get; private set; }

    public char GetCharAt(int index) => index < 0 || index >= _text.Length ? '\0' : _text[index];

    public string GetText(int startIndex, int length)
    {
        if (startIndex < 0) startIndex = 0;
        if (startIndex >= _text.Length) return string.Empty;
        if (startIndex + length > _text.Length) length = _text.Length - startIndex;
        return _text.Substring(startIndex, length);
    }

    public string GetText() => _text;

    public string GetLineText(int line)
    {
        if (line < 0 || line >= _lineStarts.Length) return string.Empty;
        int start = _lineStarts[line];
        int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
        while (end > start && (_text[end - 1] == '\n' || _text[end - 1] == '\r'))
        {
            end--;
        }
        return _text.Substring(start, end - start);
    }

    public int GetLineAt(int index)
    {
        if (_lineStarts.Length == 0) return 0;
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= index) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public int GetLineStartIndex(int line)
    {
        if (line < 0) return 0;
        if (line >= _lineStarts.Length) return _text.Length;
        return _lineStarts[line];
    }

    public int GetLineLength(int line) => GetLineText(line).Length;

    public bool TryGetWord(int index, out int wordStart, out int wordLength)
    {
        wordStart = 0;
        wordLength = 0;
        if (index < 0 || index >= _text.Length) return false;
        char c = _text[index];
        if (!IsWordChar(c)) return false;

        int s = index;
        while (s > 0 && IsWordChar(_text[s - 1])) s--;
        int e = index;
        while (e < _text.Length && IsWordChar(_text[e])) e++;
        wordStart = s;
        wordLength = e - s;
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static int[] ComputeLineStarts(string text)
    {
        if (text.Length == 0) return new[] { 0 };
        int count = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
        }
        int[] starts = new int[count];
        starts[0] = 0;
        int idx = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && idx < count)
            {
                starts[idx++] = i + 1;
            }
        }
        return starts;
    }
}

internal sealed class InMemoryDocument : ISystemVerilogDocument
{
    public InMemoryDocument(InMemoryFile file)
    {
        File = file;
        Root = new RootBlock(file);
    }

    public ISystemVerilogFile File { get; }
    public ISystemVerilogBuildingBlock Root { get; }

    public IReadOnlyList<ISystemVerilogDiagnostic> Diagnostics =>
        ((InMemoryFile)File).Diagnostics;

    public ISystemVerilogNamedElement? FindElementAt(int index) =>
        ((InMemoryFile)File).FindElementAt(index);
}

internal sealed class RootBlock : ISystemVerilogBuildingBlock
{
    public RootBlock(InMemoryFile file)
    {
        File = file;
    }

    public string Name => "$root";
    public SystemVerilogBuildingBlockKind Kind => SystemVerilogBuildingBlockKind.Root;
    public SystemVerilogRange? DefinitionRange => null;
    public ISystemVerilogFile? File { get; }

    public IReadOnlyDictionary<string, ISystemVerilogBuildingBlock> BuildingBlocks
    {
        get
        {
            // The RootBlock forwards to the file's building-block table so
            // LSP consumers see the same blocks the editor would render.
            // A small wrapper dictionary is allocated on every access to
            // keep the surface immutable; the in-memory core is only used
            // for tests and the simple language server, so the cost is
            // negligible.
            InMemoryFile mem = (InMemoryFile)File!;
            return new Dictionary<string, ISystemVerilogBuildingBlock>(mem.TopLevelBlocks
                .ToDictionary(b => b.Name, b => b));
        }
    }

    public IReadOnlyList<ISystemVerilogNamedElement> Members
    {
        get
        {
            InMemoryFile mem = (InMemoryFile)File!;
            return mem.Members;
        }
    }

    public ISystemVerilogBuildingBlock? Owner => null;

    SystemVerilogNamedElementKind ISystemVerilogNamedElement.Kind => SystemVerilogNamedElementKind.Unknown;

    public IReadOnlyList<ISystemVerilogAutocompleteItem> AutocompleteItems
    {
        get
        {
            InMemoryFile mem = (InMemoryFile)File!;
            List<ISystemVerilogAutocompleteItem> items = new();
            foreach (ISystemVerilogNamedElement member in mem.Members)
            {
                items.Add(new SystemVerilogAutocompleteItem(member.Name, SystemVerilogAutocompleteItemKind.Unknown));
            }
            return items;
        }
    }
}
