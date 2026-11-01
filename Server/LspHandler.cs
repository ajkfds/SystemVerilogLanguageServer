using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SystemVerilogCore;
using SystemVerilogCore.Documents;
using SystemVerilogCore.Diagnostics;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// Dispatcher from LSP <c>method</c> names to handlers. Handlers are
/// synchronous for now; the project-level adapters used by the language
/// server cache everything in memory and the round-trip to the parser
/// (if any) is the responsibility of the adapter, not the dispatcher.
/// </summary>
public sealed class LspHandler
{
    private readonly InMemorySystemVerilogCore _core;
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, object?>> _requestHandlers = new();
    private readonly Dictionary<string, Action<JsonElement, CancellationToken>> _notificationHandlers = new();

    /// <summary>
    /// Injected by the host (Program.cs) so the handler can push server-initiated
    /// notifications such as <c>textDocument/publishDiagnostics</c>. Null until
    /// the host wires it; pushing is skipped while null.
    /// </summary>
    public Action<LspMessage>? NotificationSender { get; set; }

    /// <summary>
    /// Workspace root folders reported by the client in <c>initialize</c>
    /// (absolute paths). Files with .sv/.svh/.v/.vh extensions under these
    /// roots are loaded from disk so that cross-file references resolve even
    /// for documents that were never opened in the editor.
    /// </summary>
    private readonly List<string> _workspaceRoots = new();
    private bool _workspaceLoaded;

    public LspHandler(InMemorySystemVerilogCore core)
    {
        _core = core;
        RegisterDefaultHandlers();
    }

    private void RegisterDefaultHandlers()
    {
        _requestHandlers["initialize"] = HandleInitialize;
        _requestHandlers["shutdown"] = HandleShutdown;
        _requestHandlers["textDocument/definition"] = HandleDefinition;
        _requestHandlers["textDocument/references"] = HandleReferences;
        _requestHandlers["textDocument/documentSymbol"] = HandleDocumentSymbol;
        _requestHandlers["textDocument/hover"] = HandleHover;
        _requestHandlers["textDocument/completion"] = HandleCompletion;
        _requestHandlers["textDocument/semanticTokens/full"] = HandleSemanticTokens;
        // custom (non-standard) requests used by the VS Code tree view.
        // LSP allows implementation-defined methods as long as they use the
        // "$/" free-for-all namespace or a vendor prefix; "rtl/" is ours.
        _requestHandlers["rtl/hierarchy"] = HandleHierarchy;

        _notificationHandlers["initialized"] = HandleInitialized;
        _notificationHandlers["exit"] = HandleExit;
        _notificationHandlers["textDocument/didOpen"] = HandleDidOpen;
        _notificationHandlers["textDocument/didChange"] = HandleDidChange;
        _notificationHandlers["textDocument/didClose"] = HandleDidClose;
        _notificationHandlers["textDocument/didSave"] = HandleDidSave;
    }

    public bool TryHandleRequest(string method, JsonElement parameters, CancellationToken cancellationToken, out object? result)
    {
        if (_requestHandlers.TryGetValue(method, out var handler))
        {
            result = handler(parameters, cancellationToken);
            return true;
        }
        result = null;
        return false;
    }

    public bool TryHandleNotification(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (_notificationHandlers.TryGetValue(method, out var handler))
        {
            handler(parameters, cancellationToken);
            return true;
        }
        return false;
    }

    // ---------------------- initialize / lifecycle ----------------------

    private object? HandleInitialize(JsonElement parameters, CancellationToken cancellationToken)
    {
        CollectWorkspaceRoots(parameters);
        LoadWorkspaceFiles(cancellationToken);
        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Full,
                },
                HoverProvider = true,
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
                CompletionProvider = new CompletionCapability
                {
                    TriggerCharacters = new List<string> { ".", "`", "$" },
                },
                SemanticTokensProvider = new SemanticTokensCapability
                {
                    Legend = new SemanticTokensLegend
                    {
                        TokenTypes = new List<string>
                        {
                            "keyword", "comment", "string", "number", "macro",
                            "function", "type", "variable", "property", "parameter", "register", "identifier",
                        },
                        TokenModifiers = new List<string>(),
                    },
                    Full = true,
                },
            },
            ServerInfo = new ServerInfo { Name = "SystemVerilogLanguageServer", Version = "0.1.0" },
        };
    }

    /// <summary>
    /// Reads rootUri / workspaceFolders from the initialize params and
    /// stores their absolute paths for the workspace scan.
    /// </summary>
    private void CollectWorkspaceRoots(JsonElement parameters)
    {
        if (parameters.TryGetProperty("rootUri", out var rootUri) && rootUri.ValueKind == JsonValueKind.String)
        {
            string? path = UriToAbsolutePath(rootUri.GetString());
            if (!string.IsNullOrEmpty(path)) _workspaceRoots.Add(path);
        }
        if (parameters.TryGetProperty("rootPath", out var rootPath) && rootPath.ValueKind == JsonValueKind.String)
        {
            string? path = UriToAbsolutePath(rootPath.GetString());
            if (!string.IsNullOrEmpty(path)) _workspaceRoots.Add(path);
        }
        if (parameters.TryGetProperty("workspaceFolders", out var folders) && folders.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in folders.EnumerateArray())
            {
                if (folder.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    string? path = UriToAbsolutePath(uri.GetString());
                    if (!string.IsNullOrEmpty(path)) _workspaceRoots.Add(path);
                }
            }
        }
    }

    /// <summary>
    /// Scans the workspace roots recursively and loads every SystemVerilog
    /// / Verilog source file from disk into the in-memory project so
    /// cross-file definitions resolve even for never-opened documents.
    /// </summary>
    private void LoadWorkspaceFiles(CancellationToken cancellationToken)
    {
        if (_workspaceLoaded) return;
        _workspaceLoaded = true;

        InMemoryProject project = _core.GetOrCreateProject("default");
        foreach (string root in _workspaceRoots)
        {
            if (!project.WorkspaceRoots.Contains(root)) project.WorkspaceRoots.Add(root);
        }
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".sv", ".svh", ".v", ".vh" };

        foreach (string root in _workspaceRoots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                });
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(file);
                if (!extensions.Contains(ext)) continue;

                // skip build outputs / vcs work dirs that may contain huge dumps
                string dirName = Path.GetFileName(Path.GetDirectoryName(file)) ?? string.Empty;
                if (dirName is "obj" or "bin" or "publish" or "node_modules") continue;

                try
                {
                    string text = File.ReadAllText(file);
                    bool isSystemVerilog = ext is ".sv" or ".svh";
                    string uri = PathToUri(file);
                    project.AddOrUpdateFile(uri, file, text, isSystemVerilog);
                }
                catch (IOException)
                {
                    // unreadable file: skip
                }
                catch (UnauthorizedAccessException)
                {
                    // unreadable file: skip
                }
            }
        }

        ParseWorkspaceFiles(project, cancellationToken);
    }

    /// <summary>
    /// Parses every loaded workspace file so their building blocks are
    /// registered into the project-wide plugin name spaces
    /// (DefinitionNameSpace / PackageNameSpace / UnitNameSpace). Without this,
    /// only documents the client happened to query would be parsed and modules
    /// declared in other files would stay unresolved ("unfound module").
    ///
    /// <para>
    /// Two passes are run: the first registers the building blocks of all
    /// files, the second re-parses the files that still report errors so that
    /// cross-file references (which could not resolve while the dependency was
    /// still unregistered) get resolved. This mirrors the editor's multi-pass
    /// ParseHierarchy.
    /// </para>
    /// </summary>
    private void ParseWorkspaceFiles(InMemoryProject project, CancellationToken cancellationToken)
    {
        List<InMemoryFile> files = new();
        foreach (ISystemVerilogFile f in project.Files)
        {
            if (f is InMemoryFile mem) files.Add(mem);
        }

        // pass 1: register every file's building blocks
        foreach (InMemoryFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.ForceBuild();
        }

        // pass 2: re-parse the files that still report errors so cross-file
        // references resolve now that every dependency is registered.
        foreach (InMemoryFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.RebuildIfHasErrors();
        }

        Console.Error.WriteLine($"workspace: {files.Count} SystemVerilog/Verilog files parsed");
    }

    /// <summary>
    /// Converts an LSP document URI to an absolute local path, handling the
    /// Windows drive-letter form (<c>file:///D:/x/y.sv</c>) and percent
    /// escaping.
    /// </summary>
    private static string? UriToAbsolutePath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Uri handles drive letters, percent escapes and forward
                // slashes uniformly on both Windows and Unix.
                Uri u = new Uri(uri);
                return u.LocalPath;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        return uri;
    }

    /// <summary>
    /// Converts an absolute local path back into a canonical file:// URI
    /// that round-trips with <see cref="UriToAbsolutePath"/>.
    /// </summary>
    private static string PathToUri(string absolutePath)
    {
        return new Uri(absolutePath).AbsoluteUri;
    }

    private object? HandleShutdown(JsonElement parameters, CancellationToken cancellationToken) => null;

    private void HandleInitialized(JsonElement parameters, CancellationToken cancellationToken)
    {
    }

    private void HandleExit(JsonElement parameters, CancellationToken cancellationToken)
    {
        Environment.Exit(0);
    }

    // ---------------------- textDocument/* ----------------------

    private void HandleDidOpen(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var textDocumentProp)) return;
        if (!textDocumentProp.TryGetProperty("uri", out var uriProp)) return;
        if (!textDocumentProp.TryGetProperty("text", out var textProp)) return;

        string uri = uriProp.GetString() ?? string.Empty;
        string text = textProp.GetString() ?? string.Empty;
        string languageId = textDocumentProp.TryGetProperty("languageId", out var lang) ? lang.GetString() ?? "systemverilog" : "systemverilog";
        bool isSystemVerilog = languageId.IndexOf("systemverilog", StringComparison.OrdinalIgnoreCase) >= 0;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        InMemoryFile file = project.AddOrUpdateFile(uri, UriToAbsolutePath(uri), text, isSystemVerilog);
        // parse eagerly so the first semantic-tokens / hover / definition
        // query already sees real-parser results (not the lightweight fallback)
        file.ForceBuild();
        PushDiagnostics(project, uri);
    }

    private void HandleDidChange(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        if (!parameters.TryGetProperty("contentChanges", out var changes)) return;

        string uri = uriProp.GetString() ?? string.Empty;
        if (changes.ValueKind != JsonValueKind.Array) return;
        if (changes.GetArrayLength() == 0) return;

        var first = changes[0];
        if (!first.TryGetProperty("text", out var textProp)) return;
        string text = textProp.GetString() ?? string.Empty;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        ISystemVerilogFile? existing = project.FindFile(uri);
        bool isSystemVerilog = existing?.IsSystemVerilog ?? true;
        InMemoryFile file = project.AddOrUpdateFile(uri, UriToAbsolutePath(uri), text, isSystemVerilog);
        // parse eagerly so the first query after an edit sees real-parser results
        file.ForceBuild();
        PushDiagnostics(project, uri);
    }

    private void HandleDidClose(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        string uri = uriProp.GetString() ?? string.Empty;
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        project.RemoveFile(uri);
    }

    /// <summary>
    /// The client sends the saved text with <c>textDocument/didSave</c>.
    /// Re-parsing here keeps the workspace name spaces in sync with the file on
    /// disk, which matters when a save changes a module signature that other
    /// files instantiate (the didChange handler already re-parses on edit, so
    /// this is mostly about refreshing diagnostics / hierarchy on save).
    /// </summary>
    private void HandleDidSave(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        string uri = uriProp.GetString() ?? string.Empty;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        ISystemVerilogFile? existing = project.FindFile(uri);
        if (existing is not InMemoryFile mem) return;

        string text = parameters.TryGetProperty("text", out var textProp)
            ? textProp.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrEmpty(text) && text != mem.CodeDocumentText)
        {
            // text changed outside didChange (e.g. format-on-save): update it
            mem.SetText(text);
        }
        mem.Rebuild();
        PushDiagnostics(project, uri);
    }

    private object? HandleDefinition(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return Array.Empty<Location>();
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);

        // manually registered symbols take precedence
        ISystemVerilogNamedElement? manual = (file as InMemoryFile)?.FindManualElementAt(index);
        if (manual?.DefinitionRange is { } mr)
        {
            return new[] { ToLocation(file, mr) };
        }

        // element at the caret (declaration or reference)
        InMemoryElement? at = (file as InMemoryFile)?.FindElementAt(index);
        if (at == null) return Array.Empty<Location>();

        // resolve the declaration (same-file first, then project-wide)
        ISystemVerilogNamedElement? def;
        if (!at.IsReference)
        {
            def = at;
        }
        else
        {
            def = at.ResolveDeclaration() ?? at;
        }
        if (def is InMemoryElement de && de.DefinitionRange is { } r && de.File is { } df)
        {
            return new[] { ToLocation(df, r) };
        }
        return Array.Empty<Location>();
    }

    private object? HandleReferences(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return Array.Empty<Location>();
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        IReadOnlyList<ISystemVerilogNamedElement> refs = project.FindReferences(file, index);
        List<Location> locations = new();
        foreach (ISystemVerilogNamedElement r in refs)
        {
            if (r.DefinitionRange is { } range && r.File is { } f)
            {
                locations.Add(ToLocation(f, range));
            }
        }
        return locations;
    }

    /// <summary>
    /// Publishes parse diagnostics for a file. Called after didOpen /
    /// didChange so the client Problems panel reflects the lightweight
    /// parser result.
    /// </summary>

    private object? HandleDocumentSymbol(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return Array.Empty<DocumentSymbol>();
        if (!td.TryGetProperty("uri", out var uriProp)) return Array.Empty<DocumentSymbol>();
        string uri = uriProp.GetString() ?? string.Empty;
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return Array.Empty<DocumentSymbol>();
        ISystemVerilogDocument? doc = project.GetDocument(file);
        if (doc == null) return Array.Empty<DocumentSymbol>();

        // The in-memory core returns the file's synthetic root; the
        // parser-backed adapter returns the same Root populated with the
        // parsed top-level building blocks. We always start with the root
        // and then flatten building blocks and their members as children
        // so editors that know about the LSP hierarchy see a real tree.
        List<DocumentSymbol> symbols = new List<DocumentSymbol>();
        DocumentSymbol root = new DocumentSymbol
        {
            Name = doc.Root.Name,
            Kind = SymbolKind.Namespace,
            Range = ToLspRange(file, new SystemVerilogRange(0, file.CodeDocument.Length)),
            SelectionRange = ToLspRange(file, new SystemVerilogRange(0, 0)),
        };

        List<DocumentSymbol> rootChildren = new List<DocumentSymbol>();
        foreach (ISystemVerilogBuildingBlock block in doc.Root.BuildingBlocks.Values)
        {
            DocumentSymbol blockSymbol = ToDocumentSymbol(file, block, SymbolKind.Module, SymbolKind.Package, SymbolKind.Class, SymbolKind.Interface);
            AppendMembers(file, block, blockSymbol);
            AppendNestedBlocks(file, block, blockSymbol);
            rootChildren.Add(blockSymbol);
        }
        if (rootChildren.Count > 0) root.Children = rootChildren;

        symbols.Add(root);
        return symbols;
    }

    /// <summary>
    /// Walks the building block hierarchy recursively and adds every
    /// sub-block to <paramref name="parent"/>. Children is lazily created
    /// so that leaf blocks do not emit an empty list.
    /// </summary>
    private static void AppendNestedBlocks(
        ISystemVerilogFile file,
        ISystemVerilogBuildingBlock block,
        DocumentSymbol parent)
    {
        if (block.BuildingBlocks.Count == 0) return;
        List<DocumentSymbol> children = parent.Children ?? new List<DocumentSymbol>();
        foreach (ISystemVerilogBuildingBlock child in block.BuildingBlocks.Values)
        {
            DocumentSymbol childSymbol = ToDocumentSymbol(file, child, SymbolKind.Module, SymbolKind.Package, SymbolKind.Class, SymbolKind.Interface);
            AppendMembers(file, child, childSymbol);
            AppendNestedBlocks(file, child, childSymbol);
            children.Add(childSymbol);
        }
        parent.Children = children;
    }

    /// <summary>
    /// Adds every directly-declared named element of the block as a child
    /// symbol. The kind is mapped from the LSP-friendly
    /// <see cref="SystemVerilogNamedElementKind"/>.
    /// </summary>
    private static void AppendMembers(
        ISystemVerilogFile file,
        ISystemVerilogBuildingBlock block,
        DocumentSymbol parent)
    {
        if (block.Members.Count == 0) return;
        List<DocumentSymbol> children = parent.Children ?? new List<DocumentSymbol>();
        foreach (ISystemVerilogNamedElement member in block.Members)
        {
            DocumentSymbol memberSymbol = new DocumentSymbol
            {
                Name = string.IsNullOrEmpty(member.Name) ? "<anonymous>" : member.Name,
                Kind = ToSymbolKind(member.Kind),
                Range = member.DefinitionRange.HasValue
                    ? ToLspRange(file, member.DefinitionRange.Value)
                    : ToLspRange(file, new SystemVerilogRange(0, 0)),
                SelectionRange = member.DefinitionRange.HasValue
                    ? ToLspRange(file, member.DefinitionRange.Value)
                    : ToLspRange(file, new SystemVerilogRange(0, 0)),
            };
            children.Add(memberSymbol);
        }
        parent.Children = children;
    }

    private static DocumentSymbol ToDocumentSymbol(
        ISystemVerilogFile file,
        ISystemVerilogBuildingBlock block,
        SymbolKind moduleKind,
        SymbolKind packageKind,
        SymbolKind classKind,
        SymbolKind interfaceKind)
    {
        SystemVerilogRange range = block.DefinitionRange ?? new SystemVerilogRange(0, 0);
        return new DocumentSymbol
        {
            Name = string.IsNullOrEmpty(block.Name) ? "<anonymous>" : block.Name,
            Kind = block.Kind switch
            {
                SystemVerilogBuildingBlockKind.Module => moduleKind,
                SystemVerilogBuildingBlockKind.Interface => interfaceKind,
                SystemVerilogBuildingBlockKind.Package => packageKind,
                SystemVerilogBuildingBlockKind.Class => classKind,
                SystemVerilogBuildingBlockKind.Primitive => SymbolKind.Namespace,
                SystemVerilogBuildingBlockKind.Program => SymbolKind.Namespace,
                SystemVerilogBuildingBlockKind.Checker => SymbolKind.Namespace,
                SystemVerilogBuildingBlockKind.GenerateBlock => SymbolKind.Namespace,
                _ => SymbolKind.Namespace,
            },
            Range = ToLspRange(file, range),
            SelectionRange = ToLspRange(file, range),
        };
    }

    private static SymbolKind ToSymbolKind(SystemVerilogNamedElementKind kind)
    {
        return kind switch
        {
            SystemVerilogNamedElementKind.Module => SymbolKind.Module,
            SystemVerilogNamedElementKind.Interface => SymbolKind.Interface,
            SystemVerilogNamedElementKind.Package => SymbolKind.Package,
            SystemVerilogNamedElementKind.Class => SymbolKind.Class,
            SystemVerilogNamedElementKind.Program => SymbolKind.Namespace,
            SystemVerilogNamedElementKind.Checker => SymbolKind.Namespace,
            SystemVerilogNamedElementKind.Primitive => SymbolKind.Namespace,
            SystemVerilogNamedElementKind.Function => SymbolKind.Function,
            SystemVerilogNamedElementKind.Task => SymbolKind.Method,
            SystemVerilogNamedElementKind.Variable => SymbolKind.Variable,
            SystemVerilogNamedElementKind.Net => SymbolKind.Variable,
            SystemVerilogNamedElementKind.Port => SymbolKind.Variable,
            SystemVerilogNamedElementKind.Parameter => SymbolKind.Constant,
            SystemVerilogNamedElementKind.LocalParameter => SymbolKind.Constant,
            SystemVerilogNamedElementKind.Typedef => SymbolKind.Class,
            SystemVerilogNamedElementKind.Modport => SymbolKind.Interface,
            SystemVerilogNamedElementKind.Instance => SymbolKind.Variable,
            SystemVerilogNamedElementKind.PackageItem => SymbolKind.Variable,
            SystemVerilogNamedElementKind.Macro => SymbolKind.Constant,
            SystemVerilogNamedElementKind.GenerateBlock => SymbolKind.Namespace,
            _ => SymbolKind.Variable,
        };
    }

    private object? HandleHover(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryGetFileAndIndex(parameters, out var file, out int index)) return null;
        InMemoryProject project = GetOrCreateProjectForUri(file.Id);
        ISystemVerilogNamedElement? def = project.FindDefinition(file, index);
        if (def == null) return null;

        // HoverContent.Build defers to a custom IHoverContentProvider when
        // one has been installed process-wide. The plugin registers a
        // richer provider that adds data type, bit width, port direction
        // and port list. The default fallback (no provider) still emits a
        // useful markdown block.
        string? content = HoverContent.Build(def);
        if (content == null) return null;

        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = "markdown",
                Value = content,
            },
            Range = def.DefinitionRange is { } r ? ToLspRange(file, r) : null,
        };
    }

    // ---------------------- semantic tokens ----------------------

    private object? HandleCompletion(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return null;
        if (!td.TryGetProperty("uri", out var uriProp)) return null;
        string uri = uriProp.GetString() ?? string.Empty;
        if (!parameters.TryGetProperty("position", out var pos)) return null;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        ISystemVerilogFile? found = project.FindFile(uri);
        if (found == null) return null;

        int line = pos.TryGetProperty("line", out var lineProp) ? lineProp.GetInt32() : 0;
        int character = pos.TryGetProperty("character", out var charProp) ? charProp.GetInt32() : 0;
        int lineStart = found.CodeDocument.GetLineStartIndex(line);
        int index = lineStart + character;

        // LSP positions are UTF-16 code-unit based, the same as .NET string
        // indices. Clamp into the document (the caret may sit at the very
        // end of the text, which is a valid completion position).
        if (index > found.CodeDocument.Length) index = found.CodeDocument.Length;

        IReadOnlyList<pluginVerilog.CoreBridge.CompletionAdapter.CompletionEntry>? entries =
            project.GetCompletionItems(found, index);
        if (entries == null) return null;

        List<CompletionItem> items = new();
        foreach (var e in entries)
        {
            items.Add(new CompletionItem
            {
                Label = e.Text,
                Kind = e.Kind,
                Detail = e.Detail,
            });
        }
        return new CompletionList { IsIncomplete = false, Items = items };
    }

    private object? HandleSemanticTokens(JsonElement parameters, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("HandleSemanticTokens called");
        if (!parameters.TryGetProperty("textDocument", out var td)) return new SemanticTokens { Data = new List<int>() };
        if (!td.TryGetProperty("uri", out var uriProp)) return new SemanticTokens { Data = new List<int>() };
        string uri = uriProp.GetString() ?? string.Empty;
        InMemoryProject project = GetOrCreateProjectForUri(uri);
        if (project.FindFile(uri) is not InMemoryFile file) return new SemanticTokens { Data = new List<int>() };

        List<TokenInfo> tokens = file.GetTokens();

        // sort by start offset, then build LSP relative encoding
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        ISystemVerilogCodeDocument doc = file.CodeDocument;
        List<int> data = new();
        int prevLine = 0;
        int prevChar = 0;
        foreach (TokenInfo t in tokens)
        {
            int startLine = doc.GetLineAt(t.Start);
            int startChar = t.Start - doc.GetLineStartIndex(startLine);
            int endLine = doc.GetLineAt(t.End);
            // multi-line tokens are clamped to their first line (LSP limitation)
            int endChar = (endLine == startLine) ? t.End - doc.GetLineStartIndex(endLine)
                : doc.GetLineLength(startLine);
            if (endChar <= startChar) continue;

            int deltaLine = startLine - prevLine;
            int deltaChar = (startLine == prevLine) ? startChar - prevChar : startChar;
            data.Add(deltaLine);
            data.Add(deltaChar);
            data.Add(endChar - startChar);
            data.Add((int)t.Type);
            data.Add(0); // no modifiers
            prevLine = startLine;
            prevChar = startChar;
        }
        Console.Error.WriteLine($"semanticTokens: {data.Count / 5} tokens for {uri}");
        return new SemanticTokens { Data = data };
    }

    // ---------------------- rtl/hierarchy (custom request) ----------------------

    /// <summary>
    /// Custom request used by the VS Code "RTL Hierarchy" tree view.
    ///
    /// <para>
    /// The VS Code File Explorer cannot be extended with extra child nodes
    /// (no provider API), so the extension renders the NavigatePanel-equivalent
    /// tree in its own Custom Tree View. This handler returns that tree.
    /// </para>
    ///
    /// <para>
    /// Params: <c>{ "uri": "file:///...", "nodePath": [0,1,0] }</c>.
    /// An empty / missing <c>nodePath</c> asks for the top level (the
    /// workspace file/folder list). Each element of <c>nodePath</c> is an
    /// index into the <c>children</c> array returned for its parent node, so
    /// the extension can expand the tree lazily: one request per expanded
    /// node, no need to serialise the whole hierarchy at once.
    /// </para>
    ///
    /// Response: <c>{ "nodes": [ RtlHierarchyNode, ... ] }</c>.
    /// </summary>
    private object? HandleHierarchy(JsonElement parameters, CancellationToken cancellationToken)
    {
        // nodePath = sequence of child indexes from the workspace root
        List<int> nodePath = new();
        if (parameters.TryGetProperty("nodePath", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement e in pathElement.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Number) nodePath.Add(e.GetInt32());
            }
        }

        // file of the node being expanded (null for the workspace root)
        string? fileUri = null;
        if (parameters.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String)
        {
            fileUri = uriElement.GetString();
        }

        InMemoryProject project = _core.GetOrCreateProject("default");

        if (nodePath.Count == 0 && string.IsNullOrEmpty(fileUri))
        {
            // top level: workspace files
            return new HierarchyResult { Nodes = BuildWorkspaceNodes(project) };
        }

        if (string.IsNullOrEmpty(fileUri) || project.FindFile(fileUri) is not InMemoryFile file)
        {
            return new HierarchyResult { Nodes = new List<HierarchyNode>() };
        }

        return new HierarchyResult { Nodes = BuildFileNodes(file, nodePath) };
    }

    /// <summary>
    /// Top level of the hierarchy view: every workspace SystemVerilog / Verilog
    /// file, in path order (mirrors the editor's NavigatePanel project tree).
    /// </summary>
    private static List<HierarchyNode> BuildWorkspaceNodes(InMemoryProject project)
    {
        List<HierarchyNode> nodes = new();
        IEnumerable<InMemoryFile> files = project.Files
            .OfType<InMemoryFile>()
            .OrderBy(f => f.AbsolutePath ?? f.Id, StringComparer.OrdinalIgnoreCase);

        foreach (InMemoryFile file in files)
        {
            string path = file.AbsolutePath ?? file.Id;
            nodes.Add(new HierarchyNode
            {
                Label = Path.GetFileName(path),
                Uri = file.Id,
                Kind = HierarchyNodeKind.File,
                Detail = GetRelativePath(project, path),
                HasChildren = file.GetHierarchyRootCount() > 0,
            });
        }
        return nodes;
    }

    /// <summary>
    /// Children of the node identified by <paramref name="nodePath"/> in the
    /// given file. Works purely on the lazily built symbol list so the tree does
    /// not have to be materialised ahead of time.
    /// </summary>
    private static List<HierarchyNode> BuildFileNodes(InMemoryFile file, List<int> nodePath)
    {
        // an empty nodePath means "the first level of this file"
        List<HierarchyNodeDescriptor> children =
            nodePath.Count == 0 ? file.GetHierarchyRoots() : ResolveChildren(file, nodePath);

        List<HierarchyNode> result = new();
        foreach (HierarchyNodeDescriptor child in children)
        {
            result.Add(new HierarchyNode
            {
                Label = child.Label,
                Kind = HierarchyNodeKind.Instance,
                Detail = child.Detail,
                Uri = child.Uri,
                StartLine = child.StartLine,
                StartCharacter = child.StartCharacter,
                HasChildren = child.Children.Count > 0,
            });
        }
        return result;
    }

    /// <summary>
    /// Walks the <paramref name="nodePath"/> (a sequence of child indexes from
    /// the file's first level) and returns the children of the addressed node.
    /// Returns an empty list when the path does not resolve (the file changed
    /// between the request that produced the path and this one).
    /// </summary>
    private static List<HierarchyNodeDescriptor> ResolveChildren(InMemoryFile file, List<int> nodePath)
    {
        List<HierarchyNodeDescriptor> level = file.GetHierarchyRoots();

        HierarchyNodeDescriptor? node = null;
        foreach (int index in nodePath)
        {
            if (index < 0 || index >= level.Count) return new List<HierarchyNodeDescriptor>();
            node = level[index];
            level = node.Children;
        }
        return node?.Children ?? new List<HierarchyNodeDescriptor>();
    }

    /// <summary>
    /// Workspace-relative path of an absolute file path, for the tree node
    /// description (falls back to the file name when no workspace root matches).
    /// </summary>
    private static string? GetRelativePath(InMemoryProject project, string absolutePath)
    {
        foreach (string root in project.WorkspaceRoots)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return absolutePath.Substring(normalizedRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
            }
        }
        return null;
    }

    // ---------------------- diagnostics push ----------------------

    /// <summary>
    /// Publishes the document's diagnostics to the client as a
    /// <c>textDocument/publishDiagnostics</c> notification. A no-op until
    /// the host injects a <see cref="NotificationSender"/>.
    /// </summary>
    private void PushDiagnostics(InMemoryProject project, string uri)
    {
        Action<LspMessage>? sender = NotificationSender;
        if (sender == null) return;

        List<Diagnostic> diagnostics = new();
        if (project.FindFile(uri) is InMemoryFile file)
        {
            ISystemVerilogDocument? doc = project.GetDocument(file);
            if (doc != null)
            {
                foreach (ISystemVerilogDiagnostic d in doc.Diagnostics)
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Range = ToLspRange(file, d.Range),
                        Severity = d.Severity switch
                        {
                            SystemVerilogSeverity.Error => 1,
                            SystemVerilogSeverity.Warning => 2,
                            SystemVerilogSeverity.Information => 3,
                            _ => 4,
                        },
                        Message = d.Message,
                        Code = d.Code,
                    });
                }
            }
        }
        sender(new LspMessage
        {
            Method = "textDocument/publishDiagnostics",
            Params = new PublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = diagnostics,
            },
        });
    }

    // ---------------------- helpers ----------------------

    private bool TryGetFileAndIndex(JsonElement parameters, out ISystemVerilogFile file, out int index)
    {
        file = null!;
        index = 0;
        if (!parameters.TryGetProperty("textDocument", out var td)) return false;
        if (!td.TryGetProperty("uri", out var uriProp)) return false;
        string uri = uriProp.GetString() ?? string.Empty;
        if (!parameters.TryGetProperty("position", out var pos)) return false;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        ISystemVerilogFile? found = project.FindFile(uri);
        if (found == null) return false;

        int line = pos.TryGetProperty("line", out var lineProp) ? lineProp.GetInt32() : 0;
        int character = pos.TryGetProperty("character", out var charProp) ? charProp.GetInt32() : 0;
        int lineStart = found.CodeDocument.GetLineStartIndex(line);
        index = lineStart + character;

        // LSP positions are UTF-16 code-unit based, the same as .NET string
        // indices, so character maps 1:1. Clamp to the end of the line so a
        // caret parked at the end of a line (or beyond it) resolves to the
        // last word on the line instead of skipping into the next line.
        int lineLength = found.CodeDocument.GetLineLength(line);
        int lineEnd = lineStart + lineLength;
        if (index > lineEnd) index = lineEnd;
        if (index > found.CodeDocument.Length) index = found.CodeDocument.Length;

        // If the caret sits just after the last character of a word (the
        // usual editor caret position), step back so TryGetWord finds it.
        ISystemVerilogCodeDocument doc = found.CodeDocument;
        if ((index >= doc.Length || !char.IsLetterOrDigit(doc.GetCharAt(index))) && index > 0
            && (char.IsLetterOrDigit(doc.GetCharAt(index - 1)) || doc.GetCharAt(index - 1) == '_' || doc.GetCharAt(index - 1) == '$'))
        {
            index--;
        }

        file = found;
        return true;
    }

    private InMemoryProject GetOrCreateProjectForUri(string uri) => _core.GetOrCreateProject("default");

    private static Location ToLocation(ISystemVerilogFile file, SystemVerilogRange range)
    {
        return new Location
        {
            Uri = file.Id,
            Range = ToLspRange(file, range),
        };
    }

    private static LspRange ToLspRange(ISystemVerilogFile file, SystemVerilogRange range)
    {
        ISystemVerilogCodeDocument doc = file.CodeDocument;
        int startLine = doc.GetLineAt(range.StartIndex);
        int startCol = range.StartIndex - doc.GetLineStartIndex(startLine);
        int endIndex = range.EndIndex;
        if (endIndex > doc.Length) endIndex = doc.Length;
        int endLine = doc.GetLineAt(endIndex);
        int endCol = endIndex - doc.GetLineStartIndex(endLine);
        return new LspRange
        {
            Start = new LspPosition { Line = startLine, Character = startCol },
            End = new LspPosition { Line = endLine, Character = endCol },
        };
    }
}

// ---------------------- LSP DTOs (subset) ----------------------

public sealed class InitializeResult
{
    [JsonPropertyName("capabilities")] public ServerCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")] public ServerInfo ServerInfo { get; set; } = new();
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")] public TextDocumentSyncOptions? TextDocumentSync { get; set; }
    [JsonPropertyName("hoverProvider")] public bool HoverProvider { get; set; }
    [JsonPropertyName("definitionProvider")] public bool DefinitionProvider { get; set; }
    [JsonPropertyName("referencesProvider")] public bool ReferencesProvider { get; set; }
    [JsonPropertyName("documentSymbolProvider")] public bool DocumentSymbolProvider { get; set; }
    [JsonPropertyName("completionProvider")] public CompletionCapability? CompletionProvider { get; set; }
    [JsonPropertyName("semanticTokensProvider")] public SemanticTokensCapability? SemanticTokensProvider { get; set; }
}

public sealed class CompletionCapability
{
    [JsonPropertyName("triggerCharacters")] public List<string>? TriggerCharacters { get; set; }
    [JsonPropertyName("resolveProvider")] public bool ResolveProvider { get; set; }
}

public sealed class CompletionList
{
    [JsonPropertyName("isIncomplete")] public bool IsIncomplete { get; set; }
    [JsonPropertyName("items")] public List<CompletionItem> Items { get; set; } = new();
}

public sealed class CompletionItem
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>LSP CompletionItemKind (3=Function, 6=Variable, 9=Module, 14=Keyword).</summary>
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class TextDocumentSyncOptions
{
    [JsonPropertyName("openClose")] public bool OpenClose { get; set; }
    [JsonPropertyName("change")] public TextDocumentSyncKind Change { get; set; }
}

public enum TextDocumentSyncKind { None = 0, Full = 1, Incremental = 2 }

public sealed class ServerInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string? Version { get; set; }
}

public sealed class Location
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
}

public sealed class LspRange
{
    [JsonPropertyName("start")] public LspPosition Start { get; set; } = new();
    [JsonPropertyName("end")] public LspPosition End { get; set; } = new();
}

public sealed class LspPosition
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("character")] public int Character { get; set; }
}

public sealed class DocumentSymbol
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public SymbolKind Kind { get; set; }
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("selectionRange")] public LspRange SelectionRange { get; set; } = new();
    [JsonPropertyName("children")] public List<DocumentSymbol>? Children { get; set; }
}

public enum SymbolKind
{
    File = 1, Module = 2, Namespace = 3, Package = 4, Class = 5, Method = 6,
    Property = 7, Field = 8, Constructor = 9, Enum = 10, Interface = 11,
    Function = 12, Variable = 13, Constant = 14, String = 15, Number = 16,
}

public sealed class Hover
{
    [JsonPropertyName("contents")] public MarkupContent Contents { get; set; } = new();
    [JsonPropertyName("range")] public LspRange? Range { get; set; }
}

public sealed class PublishDiagnosticsParams
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
    [JsonPropertyName("diagnostics")] public List<Diagnostic> Diagnostics { get; set; } = new();
}

public sealed class Diagnostic
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("severity")] public int Severity { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; } = "SystemVerilogLanguageServer";
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

public sealed class SemanticTokensCapability
{
    [JsonPropertyName("legend")] public SemanticTokensLegend Legend { get; set; } = new();
    [JsonPropertyName("full")] public bool Full { get; set; }
}

public sealed class SemanticTokensLegend
{
    [JsonPropertyName("tokenTypes")] public List<string> TokenTypes { get; set; } = new();
    [JsonPropertyName("tokenModifiers")] public List<string> TokenModifiers { get; set; } = new();
}

public sealed class SemanticTokens
{
    [JsonPropertyName("data")] public List<int> Data { get; set; } = new();
}

public sealed class MarkupContent
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "plaintext";
    [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
}
