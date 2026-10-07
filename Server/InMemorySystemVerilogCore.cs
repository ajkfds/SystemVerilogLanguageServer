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

internal sealed class InMemoryProject : ISystemVerilogProject
{
    private readonly ConcurrentDictionary<string, InMemoryFile> _files = new();

    public InMemoryProject(string id)
    {
        Id = id;
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
        return file;
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
        _files.TryRemove(id, out _);
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
}

internal sealed class InMemoryFile : ISystemVerilogFile
{
    private InMemoryDocument? _document;
    private readonly List<InMemoryElement> _allElements = new();
    private readonly Dictionary<string, List<InMemoryElement>> _byName = new();
    private readonly Dictionary<string, ISystemVerilogBuildingBlock> _buildingBlocks = new();
    private readonly List<ISystemVerilogNamedElement> _members = new();
    private readonly List<ISystemVerilogDiagnostic> _diagnostics = new();
    /// <summary>Manually registered symbols (AddSymbol); take precedence over parser output.</summary>
    private readonly List<ISystemVerilogNamedElement> _manualList = new();
    private bool _manualSymbols;
    private bool _built;

    public InMemoryFile(string id, string? absolutePath, bool isSystemVerilog, string text)
    {
        Id = id;
        AbsolutePath = absolutePath;
        IsSystemVerilog = isSystemVerilog;
        CodeDocument = new InMemoryCodeDocument(text);
    }

    /// <summary>
    /// Lazily runs the lightweight parser on the document text. Skipped when
    /// symbols were registered manually (AddSymbol), so callers that build
    /// the symbol table themselves keep full control.
    /// </summary>
    private void EnsureBuilt()
    {
        if (_built || _manualSymbols) return;
        _built = true;
        BuildSymbols(((InMemoryCodeDocument)CodeDocument).GetText());
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
    }

    private pluginVerilog.Verilog.ParsedDocument? _parsedDocument;

    /// <summary>
    /// Drives the plugin ParseEngine on an in-memory VerilogFile.
    /// </summary>
    private System.Threading.Tasks.Task<pluginVerilog.Verilog.ParsedDocument?> ParseSystemVerilog(string text)
    {
        // create / reuse a plugin-side VerilogFile for this in-memory file
        pluginVerilog.Data.VerilogFile verilogFile = _verilogFile ??= CreateVerilogFile();
        return pluginVerilog.CoreBridge.ParseEngine.ParseSystemVerilogAsync(text, verilogFile);
    }

    private pluginVerilog.Data.VerilogFile? _verilogFile;
    private CodeEditor2.Data.Project? _project;

    private pluginVerilog.Data.VerilogFile CreateVerilogFile()
    {
        // register plugin file types once (UI-free)
        if (CodeEditor2.Global.FileTypes.Count == 0)
        {
            new pluginVerilog.Plugin().Register();
        }

        CodeEditor2.Data.Project project = _project ??= new ParserProjectStub();
        // Normally registered by Plugin.projectCreated (fired from
        // Project.CreateAsync); the UI-free parse path bypasses it.
        if (!project.ProjectProperties.ContainsKey(pluginVerilog.Plugin.StaticID))
        {
            project.ProjectProperties.Add(
                pluginVerilog.Plugin.StaticID,
                new pluginVerilog.ProjectProperty(project, new pluginVerilog.ProjectProperty.Setup()));
        }

        pluginVerilog.Data.VerilogFile file = new pluginVerilog.Data.VerilogFile()
        {
            Name = System.IO.Path.GetFileName(Id),
            Project = project,
            RelativePath = Id,
        };
        file.SystemVerilog = IsSystemVerilog;
        return file;
    }

    private sealed class ParserProjectStub : CodeEditor2.Data.Project
    {
        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public ParserProjectStub() : base("lsp", System.IO.Path.GetTempPath(), "") { }
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
                    new SystemVerilogRange(nameStart, nameStart + bb.Name.Length), this, parent);
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
                    new SystemVerilogRange(start, start + element.Name.Length), this, block);
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
            var element = new InMemoryElement(sym.Name, KindToNamedElementKind(sym.Kind),
                new SystemVerilogRange(sym.Start, sym.End), this, owner);
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
            var element = new InMemoryElement(rf.Name, SystemVerilogNamedElementKind.Unknown,
                new SystemVerilogRange(rf.Start, rf.End), this, null, isReference: true);
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
    public ISystemVerilogCodeDocument CodeDocument { get; }

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
            _cachedTokens = parserTokens;
            return parserTokens;
        }
        return _cachedTokens ??= LightweightParser.Parse(((InMemoryCodeDocument)CodeDocument).GetText()).Tokens;
    }

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

    internal IReadOnlyList<ISystemVerilogDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Replaces the diagnostic list after a (re)parse.</summary>
    internal void SetDiagnostics(IEnumerable<ISystemVerilogDiagnostic> diags)
    {
        _diagnostics.Clear();
        _diagnostics.AddRange(diags);
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
