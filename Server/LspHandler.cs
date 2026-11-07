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
public sealed partial class LspHandler
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

    /// <summary>
    /// URIs the client currently has open (didOpen .. didClose). Used by the
    /// workspace rescan so a file that is being edited is never dropped from
    /// the project just because it disappeared from disk temporarily.
    /// </summary>
    private readonly HashSet<string> _openUris = new();

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

        // ---- standard LSP features wired but not implemented yet ----
        // The handlers exist so the client can call them and the capability is
        // advertised; each returns an empty result until the C# implementation
        // lands (see LspHandler.Unimplemented.cs).
        _requestHandlers["textDocument/formatting"] = HandleFormatting;
        _requestHandlers["textDocument/rangeFormatting"] = HandleRangeFormatting;
        _requestHandlers["textDocument/onTypeFormatting"] = HandleOnTypeFormatting;
        _requestHandlers["textDocument/rename"] = HandleRename;
        _requestHandlers["textDocument/prepareRename"] = HandlePrepareRename;
        _requestHandlers["textDocument/codeAction"] = HandleCodeAction;
        _requestHandlers["textDocument/codeLens"] = HandleCodeLens;
        _requestHandlers["textDocument/signatureHelp"] = HandleSignatureHelp;
        _requestHandlers["textDocument/completionItem/resolve"] = HandleCompletionResolve;
        _requestHandlers["textDocument/inlayHint"] = HandleInlayHint;
        _requestHandlers["textDocument/declaration"] = HandleDeclaration;
        _requestHandlers["textDocument/typeDefinition"] = HandleTypeDefinition;
        _requestHandlers["textDocument/implementation"] = HandleImplementation;
        _requestHandlers["textDocument/documentHighlight"] = HandleDocumentHighlight;
        _requestHandlers["textDocument/foldingRange"] = HandleFoldingRange;
        _requestHandlers["textDocument/selectionRange"] = HandleSelectionRange;
        _requestHandlers["textDocument/documentLink"] = HandleDocumentLink;
        _requestHandlers["textDocument/semanticTokens/range"] = HandleSemanticTokensRange;
        _requestHandlers["workspace/symbol"] = HandleWorkspaceSymbol;
        _requestHandlers["workspace/executeCommand"] = HandleExecuteCommand;

        // custom (non-standard) requests used by the VS Code tree view.
        // LSP allows implementation-defined methods as long as they use the
        // "$/" free-for-all namespace or a vendor prefix; "rtl/" is ours.
        _requestHandlers["rtl/hierarchy"] = HandleHierarchy;
        _requestHandlers["rtl/reparse"] = HandleReparse;
        _requestHandlers["rtl/theme"] = HandleTheme;
        _requestHandlers["rtl/snippet"] = HandleSnippet;
        _requestHandlers["rtl/autoConnect"] = HandleAutoConnect;
        _requestHandlers["rtl/autoConnectApply"] = HandleAutoConnectApply;

        _notificationHandlers["initialized"] = HandleInitialized;
        _notificationHandlers["exit"] = HandleExit;
        _notificationHandlers["workspace/didChangeWorkspaceFolders"] = HandleDidChangeWorkspaceFolders;
        _notificationHandlers["textDocument/didOpen"] = HandleDidOpen;
        _notificationHandlers["textDocument/didChange"] = HandleDidChange;
        _notificationHandlers["textDocument/didClose"] = HandleDidClose;
        _notificationHandlers["textDocument/didSave"] = HandleDidSave;

        // ---- standard LSP notifications wired but not implemented yet ----
        _notificationHandlers["workspace/didChangeWatchedFiles"] = HandleDidChangeWatchedFiles;
        _notificationHandlers["workspace/didChangeConfiguration"] = HandleDidChangeConfiguration;
        _notificationHandlers["textDocument/willSave"] = HandleWillSave;
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
        // A failure while preparing the workspace must never fail the
        // initialize handshake: the client would drop the session and the RTL
        // hierarchy view would stay empty forever. Answer with capabilities and
        // degrade gracefully instead.
        try
        {
            LoadWorkspaceFiles(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"workspace preparation failed: {ex}");
        }
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
                        // shared with rtl/theme so the legend and the colour
                        // rules can never drift apart
                        TokenTypes = new List<string>(SemanticTokenTypes),
                        TokenModifiers = new List<string>(),
                    },
                    Full = true,
                    Range = true,
                },

                // ---- standard LSP features wired but not implemented yet ----
                // Advertised so the client registers the providers; the handlers
                // return empty results until the C# implementation lands.
                DocumentFormattingProvider = true,
                DocumentRangeFormattingProvider = true,
                RenameProvider = true,
                CodeActionProvider = true,
                CodeLensProvider = new CodeLensCapability(),
                SignatureHelpProvider = new SignatureHelpCapability
                {
                    TriggerCharacters = new List<string> { "(", "," },
                },
                InlayHintProvider = true,
                DeclarationProvider = true,
                TypeDefinitionProvider = true,
                ImplementationProvider = true,
                DocumentHighlightProvider = true,
                FoldingRangeProvider = true,
                SelectionRangeProvider = true,
                DocumentLinkProvider = new DocumentLinkCapability(),
                WorkspaceSymbolProvider = true,
                ExecuteCommandProvider = new ExecuteCommandCapability(),
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
        // rootUri, rootPath and workspaceFolders usually carry the same folder;
        // adding it three times would list every root three times in the tree.
        if (parameters.TryGetProperty("rootUri", out var rootUri) && rootUri.ValueKind == JsonValueKind.String)
        {
            AddWorkspaceRoot(rootUri.GetString());
        }
        if (parameters.TryGetProperty("rootPath", out var rootPath) && rootPath.ValueKind == JsonValueKind.String)
        {
            AddWorkspaceRoot(rootPath.GetString());
        }
        if (parameters.TryGetProperty("workspaceFolders", out var folders) && folders.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in folders.EnumerateArray())
            {
                if (folder.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                {
                    AddWorkspaceRoot(uri.GetString());
                }
            }
        }
    }

    /// <summary>
    /// Registers a workspace root once. Duplicates are ignored so a client that
    /// reports the same folder via <c>rootUri</c> and <c>workspaceFolders</c>
    /// does not produce a repeated entry in the tree.
    /// </summary>
    private void AddWorkspaceRoot(string? uriOrPath)
    {
        string? path = UriToAbsolutePath(uriOrPath);
        if (string.IsNullOrEmpty(path)) return;

        string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(normalized)) return;

        foreach (string existing in _workspaceRoots)
        {
            string trimmed = existing.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(trimmed, normalized, StringComparison.OrdinalIgnoreCase)) return;
        }
        _workspaceRoots.Add(normalized);
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

        // The plugin item tree is keyed by the workspace-relative path, so it
        // can only be built once the roots are known. `include resolution goes
        // through that tree (WordScanner.parseInclude), so without it every
        // include reports "file not found" in the LSP host.
        project.RebuildPluginItemTree();

        // initialize must answer immediately: reading and parsing every source
        // file of a large workspace (this repository has >1000 .sv files under
        // verilog_sample/sv_tests) blocked the initialize response for minutes,
        // so the client never got a session and the RTL hierarchy view stayed
        // empty. The scan + parse therefore runs in the background; the
        // hierarchy tree itself is built from the directory listing and a
        // source file is parsed lazily when it is expanded.
        Console.Error.WriteLine($"workspace roots: {string.Join(", ", _workspaceRoots)}");
        if (_workspaceRoots.Count == 0) return;

        // Listing the files is cheap; parsing them is not. For a small project
        // the parse also runs here so cross-file references resolve immediately.
        // A large workspace (thousands of sources) is parsed in the background
        // instead: blocking initialize for minutes is what left the hierarchy
        // view empty, and each file is parsed lazily on expansion anyway.
        List<InMemoryFile> added = ScanWorkspaceFiles(project, CancellationToken.None);
        if (added.Count <= MaxEagerParseFiles)
        {
            ParseFiles(added, CancellationToken.None);
            return;
        }

        Console.Error.WriteLine(
            $"workspace: {added.Count} files, parsing in background (eager threshold {MaxEagerParseFiles})");
        Task.Run(() =>
        {
            try
            {
                ParseFiles(added, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"background workspace parse failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Up to this many workspace files are parsed synchronously at initialize
    /// (keeps cross-file resolution and diagnostics for normal projects). A
    /// larger workspace is parsed in the background so the initialize handshake
    /// stays fast.
    /// </summary>
    private const int MaxEagerParseFiles = 200;

    /// <summary>
    /// Re-reads the workspace roots from disk so the RTL hierarchy tree shows
    /// files created / removed after the initial scan. Files that are open in
    /// the client are left untouched (their text is the authoritative version).
    /// </summary>
    private void RescanWorkspaceFiles(InMemoryProject project)
    {
        // no root reported yet (single file opened, no folder): infer one from
        // the documents the project already knows so a refresh still fills in
        // the rest of the workspace instead of doing nothing
        if (_workspaceRoots.Count == 0) EnsureWorkspaceRootFromKnownFiles(project);
        if (_workspaceRoots.Count == 0) return;

        // Only refresh the list of known files here. The hierarchy tree is
        // built from the directory listing, so a rescan must stay cheap: parsing
        // the whole workspace on a refresh made the request time out on large
        // projects. Newly discovered files are parsed lazily when expanded.
        ScanWorkspaceFiles(project, CancellationToken.None);
    }

    /// <summary>
    /// Enumerates every SystemVerilog / Verilog file under the workspace roots
    /// and registers it in the in-memory project. Files that are currently
    /// open in the client are skipped: their buffered text is newer than the
    /// file on disk. Returns the files that were newly added or whose text
    /// changed, so the caller can parse only those.
    /// </summary>
    private List<InMemoryFile> ScanWorkspaceFiles(InMemoryProject project, CancellationToken cancellationToken)
    {
        List<InMemoryFile> changed = new();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".sv", ".svh", ".v", ".vh" };

        // directories whose contents are build output / VCS dumps: never scan
        var skippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "obj", "bin", "publish", "node_modules", ".git", ".svn", ".hg" };

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

                if (IsInSkippedDirectory(root, file, skippedDirectories)) continue;

                string uri = PathToUri(file);
                // an open document is authoritative: do not overwrite the
                // buffered (possibly unsaved) text with the on-disk copy
                if (_openUris.Contains(uri)) continue;

                try
                {
                    string text = File.ReadAllText(file);
                    bool isSystemVerilog = ext is ".sv" or ".svh";
                    InMemoryFile registered =
                        project.AddOrUpdateFileIfChanged(uri, file, text, isSystemVerilog);
                    if (!registered.IsBuilt) changed.Add(registered);
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

        PruneDeletedWorkspaceFiles(project);
        return changed;
    }

    /// <summary>
    /// Drops files that vanished from disk so the workspace tree does not keep
    /// listing stale entries after a rescan. Only files under a workspace root
    /// are considered; open documents are left alone (a document can be open
    /// while its file is temporarily missing, and its buffer is authoritative).
    /// </summary>
    private void PruneDeletedWorkspaceFiles(InMemoryProject project)
    {
        if (_workspaceRoots.Count == 0) return;

        List<string> stale = new();
        foreach (ISystemVerilogFile f in project.Files)
        {
            if (f is not InMemoryFile file) continue;
            string? path = file.AbsolutePath;
            if (string.IsNullOrEmpty(path)) continue;      // never came from disk
            if (_openUris.Contains(file.Id)) continue;     // open: keep the buffer
            if (!IsUnderWorkspaceRoot(path)) continue;     // outside the workspace
            if (File.Exists(path)) continue;

            stale.Add(file.Id);
        }

        foreach (string id in stale)
        {
            project.RemoveFile(id);
        }
    }

    /// <summary>
    /// True when <paramref name="absolutePath"/> lives under one of the
    /// workspace roots.
    /// </summary>
    private bool IsUnderWorkspaceRoot(string absolutePath)
    {
        foreach (string root in _workspaceRoots)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            if (absolutePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when any directory segment of <paramref name="file"/> below the
    /// workspace <paramref name="root"/> is a build output / VCS directory
    /// (matching only the immediate parent would miss nested cases such as
    /// <c>build/obj/x.sv</c>).
    /// </summary>
    private static bool IsInSkippedDirectory(string root, string file, HashSet<string> skipped)
    {
        string? directory = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(directory))
        {
            string name = Path.GetFileName(directory);
            if (skipped.Contains(name)) return true;
            string? parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.Ordinal)) break;
            if (string.Equals(directory, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            directory = parent;
        }
        return false;
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

        ParseFiles(files, cancellationToken);
    }

    /// <summary>
    /// Two-pass parse of <paramref name="files"/>: the first pass registers
    /// every file's building blocks, the second re-parses the files that still
    /// report errors so cross-file references resolve now that every
    /// dependency is registered.
    /// </summary>
    private static void ParseFiles(IReadOnlyList<InMemoryFile> files, CancellationToken cancellationToken)
    {
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
    /// Canonicalises a document URI so the client's didOpen / didChange form
    /// and the workspace scan form map to the same key.
    ///
    /// <para>
    /// VS Code sends the drive-letter colon percent-encoded and the drive
    /// letter lower-case (<c>file:///c%3A/Users/x</c>), while
    /// <see cref="PathToUri"/> produces <c>file:///C:/Users/x</c>. Without
    /// normalisation the same file was registered twice (once per form), so an
    /// opened document was parsed under one key while the RTL hierarchy looked
    /// it up under the other - the tree did not reflect the opened file.
    /// </para>
    /// </summary>
    private static string NormalizeUri(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return uri;
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return uri;

        // Only rewrite when the URI actually needs it: a Windows drive-letter
        // form (optionally with the colon percent-encoded / lower-case drive,
        // which is what VS Code sends). A synthetic / relative id such as
        // "file:///test.sv" is left untouched so tests and non-file hosts keep
        // stable keys.
        bool hasEscaped = uri.IndexOf('%') >= 0;
        bool isDriveForm =
            uri.Length >= 10
            && uri[7] == '/'
            && char.IsLetter(uri[8])
            && (uri[9] == ':' || (hasEscaped && uri.Length > 12 && uri[9] == '%' && uri[10] == '3' && (uri[11] == 'A' || uri[11] == 'a')));
        if (!isDriveForm && !hasEscaped) return uri;

        string? absolutePath = UriToAbsolutePath(uri);
        if (string.IsNullOrEmpty(absolutePath)) return uri;
        string canonical = PathToUri(absolutePath);
        return string.IsNullOrEmpty(canonical) ? uri : canonical;
    }

    /// <summary>
    /// Converts an LSP document URI to an absolute local path, handling the
    /// Windows drive-letter form (<c>file:///D:/x/y.sv</c>) and percent
    /// escaping.
    /// </summary>
    private static string? UriToAbsolutePath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            // already a plain path (rootPath, a synthetic test id, ...)
            return uri;
        }

        string path;
        try
        {
            Uri u = new Uri(uri);
            path = u.LocalPath;
        }
        catch (UriFormatException)
        {
            return null;
        }

        // VS Code sends the drive-letter colon percent-encoded and the drive
        // letter lower-case (file:///d%3A/Document/x). Uri.LocalPath then keeps
        // the escape / yields a "/d:/Document/x" style path, which does not
        // exist on Windows: the workspace scan found 0 files and the RTL
        // hierarchy tree stayed empty. Decode and normalise to a real path.
        if (path.IndexOf('%') >= 0)
        {
            path = Uri.UnescapeDataString(path);
        }

        if (OperatingSystem.IsWindows())
        {
            // "/d:/Document/x" -> "d:/Document/x"
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            {
                path = path.Substring(1);
            }
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                path = char.ToUpperInvariant(path[0]) + path.Substring(1);
                path = path.Replace('/', '\\');
            }
        }

        return path;
    }

    /// <summary>
    /// Converts an absolute local path back into a canonical file:// URI
    /// that round-trips with <see cref="UriToAbsolutePath"/>.
    /// </summary>
    private static string PathToUri(string absolutePath)
    {
        // A relative path (a synthetic test file, or a document the host never
        // materialised on disk) cannot be turned into a file:// URI. Fall back
        // to the raw string instead of throwing: the scan must never fail
        // because of one odd entry.
        try
        {
            return new Uri(absolutePath).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return absolutePath;
        }
    }

    /// <summary>
    /// Re-parses one file on demand so the RTL hierarchy reflects an edit even
    /// before the client sends a didChange (e.g. the user clicked a hierarchy
    /// node). Params: <c>{ "uri": "file:///..." }</c>. Returns
    /// <c>{ "parsed": true }</c>.
    /// </summary>
    private object? HandleReparse(JsonElement parameters, CancellationToken cancellationToken)
    {
        string? uri = null;
        if (parameters.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String)
        {
            uri = NormalizeUri(uriElement.GetString() ?? string.Empty);
        }
        if (string.IsNullOrEmpty(uri)) return new { parsed = false };

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        InMemoryFile? file = TryLoadFileFromDisk(project, uri);
        if (file == null) return new { parsed = false };

        // An instance node click parses the document AS that instance so the
        // parameter overrides written at the instantiation are applied (the
        // editor opens the instance, not the file, which is why its diagnostics
        // reflect the override). Falls back to the plain file parse when the
        // path does not resolve to an instance (a file node click).
        List<string>? instancePath = null;
        if (parameters.TryGetProperty("instancePathSegments", out var segmentsElement)
            && segmentsElement.ValueKind == JsonValueKind.Array)
        {
            instancePath = new List<string>();
            foreach (JsonElement segment in segmentsElement.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.String)
                {
                    instancePath.Add(segment.GetString() ?? string.Empty);
                }
            }
        }

        // The instance path is relative to the file the instantiation is
        // written in (the parent), which is not the file being opened when the
        // node is the instantiated module. The client sends it as parentUri.
        string? parentUri = null;
        if (parameters.TryGetProperty("parentUri", out var parentElement)
            && parentElement.ValueKind == JsonValueKind.String)
        {
            parentUri = NormalizeUri(parentElement.GetString() ?? string.Empty);
        }

        bool parsedAsInstance = instancePath != null
            && instancePath.Count > 0
            && file.RebuildAsInstance(parentUri, instancePath);
        if (!parsedAsInstance)
        {
            file.Rebuild();
        }
        PushDiagnostics(project, uri);
        return new { parsed = true, instance = parsedAsInstance };
    }

    private object? HandleShutdown(JsonElement parameters, CancellationToken cancellationToken) => null;

    private void HandleInitialized(JsonElement parameters, CancellationToken cancellationToken)
    {
    }

    /// <summary>
    /// The user added / removed a folder from the workspace. The RTL hierarchy
    /// view mirrors the whole workspace, so the added roots are scanned into the
    /// project and the removed ones dropped from it.
    /// </summary>
    private void HandleDidChangeWorkspaceFolders(JsonElement parameters, CancellationToken cancellationToken)
    {
        InMemoryProject project = _core.GetOrCreateProject("default");

        if (parameters.TryGetProperty("event", out var eventElement)
            && eventElement.TryGetProperty("added", out var added)
            && added.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in added.EnumerateArray())
            {
                if (!folder.TryGetProperty("uri", out var uri)) continue;
                string? path = UriToAbsolutePath(uri.GetString());
                if (string.IsNullOrEmpty(path)) continue;
                if (!_workspaceRoots.Contains(path)) _workspaceRoots.Add(path);
                if (!project.WorkspaceRoots.Contains(path)) project.WorkspaceRoots.Add(path);
            }
        }

        if (parameters.TryGetProperty("event", out var removedEvent)
            && removedEvent.TryGetProperty("removed", out var removed)
            && removed.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement folder in removed.EnumerateArray())
            {
                if (!folder.TryGetProperty("uri", out var uri)) continue;
                string? path = UriToAbsolutePath(uri.GetString());
                if (string.IsNullOrEmpty(path)) continue;
                _workspaceRoots.Remove(path);
                project.WorkspaceRoots.Remove(path);
            }
        }

        // the relative path of every known file depends on the roots, so the
        // plugin item tree (used by `include resolution) must be re-keyed
        project.RebuildPluginItemTree();
        RescanWorkspaceFiles(project);
        // the workspace layout changed: the tree must redraw
        PushHierarchyInvalidated("didChangeWorkspaceFolders");
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

        // Normalise the URI so the didOpen entry is the SAME InMemoryFile the
        // workspace scan created for this file. VS Code sends the drive-letter
        // colon percent-encoded and lower-case (file:///c%3A/...), while the
        // scan builds file:///C:/...; without normalisation the same physical
        // file ended up as two entries, so opening it (didOpen) parsed one entry
        // while the RTL hierarchy expanded the other - the tree never reflected
        // the opened file until an edit happened to touch the scanned entry.
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        string text = textProp.GetString() ?? string.Empty;
        string languageId = textDocumentProp.TryGetProperty("languageId", out var lang) ? lang.GetString() ?? "systemverilog" : "systemverilog";
        bool isSystemVerilog = languageId.IndexOf("systemverilog", StringComparison.OrdinalIgnoreCase) >= 0;

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        _openUris.Add(uri);
        InMemoryFile file = project.AddOrUpdateFile(uri, UriToAbsolutePath(uri), text, isSystemVerilog);
        // When the client opened a single file without a workspace folder
        // (no rootUri / workspaceFolders in initialize), adopt the file's
        // directory as a workspace root. Otherwise the RTL hierarchy can only
        // ever show the documents the client happens to have open.
        EnsureWorkspaceRootFromFile(uri, project);
        // parse eagerly so the first semantic-tokens / hover / definition
        // query already sees real-parser results (not the lightweight fallback)
        file.ForceBuild();
        PushDiagnostics(project, uri);
    }

    /// <summary>
    /// Fallback for clients that open a single file without opening a folder:
    /// in that case initialize carries neither <c>rootUri</c> nor
    /// <c>workspaceFolders</c>, so the workspace scan in
    /// <see cref="LoadWorkspaceFiles"/> has no root to walk and the hierarchy
    /// tree ends up listing only the opened document.
    ///
    /// <para>
    /// The directory of the first opened file is adopted as the workspace root,
    /// walking up to the enclosing project directory when one can be recognised
    /// (a <c>.git</c> / <c>.svn</c> / <c>.hg</c> entry, a solution / workspace
    /// file). The user who opened <c>src/rtl/top.sv</c> directly expects the
    /// whole project tree, not just <c>src/rtl</c>. Runs at most once; later
    /// opens already have a root.
    /// </para>
    /// </summary>
    private void EnsureWorkspaceRootFromFile(string uri, InMemoryProject project)
    {
        if (_workspaceRoots.Count > 0) return;

        string? absolutePath = UriToAbsolutePath(uri);
        if (string.IsNullOrEmpty(absolutePath)) return;
        // only a document backed by a real file on disk can tell us where the
        // project lives (an untitled buffer has no directory to scan)
        if (!File.Exists(absolutePath)) return;
        string? directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        directory = FindProjectRoot(directory);
        _workspaceRoots.Add(directory);
        if (!project.WorkspaceRoots.Contains(directory)) project.WorkspaceRoots.Add(directory);

        // the root was unknown when the file was registered, so its plugin item
        // entry (if any) was keyed by the URI; re-key the whole tree now that
        // the workspace-relative paths can be computed
        project.RebuildPluginItemTree();

        // Deliberately no scan / parse here. The hierarchy tree is built from
        // the directory listing, and parsing every source file of a large
        // project synchronously on didOpen would block the rtl/hierarchy
        // request for minutes (the tree would render empty). Files are loaded
        // and parsed lazily when a node is expanded (see TryLoadFileFromDisk).
        Console.Error.WriteLine($"workspace root inferred from opened file: {directory}");

        // the tree could not be built before the root was known: tell the
        // client to redraw now that the workspace is scannable
        PushHierarchyInvalidated("workspaceRootInferred");
    }

    /// <summary>
    /// Self-healing variant of <see cref="EnsureWorkspaceRootFromFile"/> used by
    /// the hierarchy request: when the client never sent a workspace root, take
    /// the directory of any document the project already knows about, walk up to
    /// the enclosing project directory and scan it. This covers the case where
    /// the document was opened before this feature existed (the didOpen hook was
    /// not there yet) and no rescan can be triggered from the client.
    /// </summary>
    private void EnsureWorkspaceRootFromKnownFiles(InMemoryProject project)
    {
        foreach (ISystemVerilogFile f in project.Files)
        {
            if (f is not InMemoryFile file) continue;
            string? path = file.AbsolutePath;
            if (string.IsNullOrEmpty(path)) continue;
            if (!File.Exists(path)) continue;

            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) continue;

            directory = FindProjectRoot(directory);
            if (!_workspaceRoots.Contains(directory)) _workspaceRoots.Add(directory);
            if (!project.WorkspaceRoots.Contains(directory)) project.WorkspaceRoots.Add(directory);
            project.RebuildPluginItemTree();

            // no eager scan / parse: the tree is built from the directory
            // listing and files are parsed lazily when expanded
            Console.Error.WriteLine($"workspace root inferred from known file: {directory}");

            // the tree could not be built before the root was known: tell the
            // client to redraw now that the workspace is scannable
            PushHierarchyInvalidated("workspaceRootInferred");
            return;
        }
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a directory
    /// that looks like a project root (version control metadata, a solution /
    /// workspace file, or a project file). Returns the nearest such directory,
    /// or <paramref name="startDirectory"/> when none is found. The climb is
    /// bounded so a stray source file at the drive root never scans the whole
    /// disk.
    /// </summary>
    private static string FindProjectRoot(string startDirectory)
    {
        static bool IsProjectRoot(string dir)
        {
            try
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))
                    || Directory.Exists(Path.Combine(dir, ".svn"))
                    || Directory.Exists(Path.Combine(dir, ".hg")))
                {
                    return true;
                }

                foreach (string pattern in new[] { "*.sln", "*.slnx", "*.code-workspace", "*.csproj", "*.f" })
                {
                    if (Directory.EnumerateFiles(dir, pattern).Any()) return true;
                }
            }
            catch (IOException)
            {
                // unreadable directory: treat as "not a project root"
            }
            catch (UnauthorizedAccessException)
            {
                // unreadable directory: treat as "not a project root"
            }
            return false;
        }

        string current = startDirectory;
        // bounded climb: at most 8 levels above the file's directory
        for (int depth = 0; depth < 8; depth++)
        {
            if (IsProjectRoot(current)) return current;

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) break;
            current = parent;
        }
        return startDirectory;
    }

    private void HandleDidChange(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        if (!parameters.TryGetProperty("contentChanges", out var changes)) return;

        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
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

    /// <summary>
    /// A closed document stays in the project: the RTL hierarchy view lists the
    /// whole workspace, and dropping the file on didClose made the tree shrink
    /// back to "only the files that happen to be open". The buffered text is
    /// reloaded from disk so later queries see the saved version (the file may
    /// also have been deleted, in which case it is removed).
    /// </summary>
    private void HandleDidClose(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("textDocument", out var td)) return;
        if (!td.TryGetProperty("uri", out var uriProp)) return;
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
        _openUris.Remove(uri);

        InMemoryProject project = GetOrCreateProjectForUri(uri);
        string? absolutePath = UriToAbsolutePath(uri);
        if (absolutePath == null)
        {
            project.RemoveFile(uri);
            return;
        }

        if (!File.Exists(absolutePath))
        {
            // deleted from disk while open: drop it from the tree
            project.RemoveFile(uri);
            return;
        }

        // re-read the saved content so the workspace view keeps the file with
        // its on-disk text (cross-file resolution must not keep unsaved edits)
        try
        {
            string text = File.ReadAllText(absolutePath);
            string ext = Path.GetExtension(absolutePath);
            bool isSystemVerilog = ext is ".sv" or ".svh";
            InMemoryFile file = project.AddOrUpdateFile(uri, absolutePath, text, isSystemVerilog);
            file.ForceBuild();
        }
        catch (IOException)
        {
            project.RemoveFile(uri);
        }
        catch (UnauthorizedAccessException)
        {
            project.RemoveFile(uri);
        }
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
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);

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
        // a save can change the instance hierarchy (module signature, new
        // instantiations), so let the client redraw the tree
        PushHierarchyInvalidated("didSave");
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
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
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
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
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
                InsertText = e.InsertText,
                InsertTextFormat = e.InsertText == null ? null : e.InsertTextFormat,
            });
        }
        return new CompletionList { IsIncomplete = false, Items = items };
    }

    private object? HandleSemanticTokens(JsonElement parameters, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("HandleSemanticTokens called");
        if (!parameters.TryGetProperty("textDocument", out var td)) return new SemanticTokens { Data = new List<int>() };
        if (!td.TryGetProperty("uri", out var uriProp)) return new SemanticTokens { Data = new List<int>() };
        // Normalise so the request resolves the same entry the workspace scan /
        // didOpen registered (VS Code sends the escaped, lower-case drive form
        // for semantic tokens too; without this the lookup missed and no
        // highlight tokens were returned).
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
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
    /// Params: <c>{ "uri": "file:///...", "nodePath": [0,1,0], "folderPath": "src/sub" }</c>.
    /// An empty / missing <c>nodePath</c> asks for the top level (the
    /// workspace folder tree). Each element of <c>nodePath</c> is an
    /// index into the <c>children</c> array returned for its parent node, so
    /// the extension can expand the tree lazily: one request per expanded
    /// node, no need to serialise the whole hierarchy at once.
    /// </para>
    ///
    /// <para>
    /// The top level is the whole workspace (every .sv/.svh/.v/.vh file found
    /// under the workspace roots), not just the documents the client happens
    /// to have opened: folders are returned as
    /// <see cref="HierarchyNodeKind.Folder"/> nodes so a large project stays
    /// navigable. <c>folderPath</c> expands such a folder, and
    /// <c>{ "rescan": true }</c> re-scans the workspace roots from disk
    /// (picking up files created after initialize).
    /// </para>
    ///
    /// Response: <c>{ "nodes": [ RtlHierarchyNode, ... ] }</c>.
    /// </summary>
    private object? HandleHierarchy(JsonElement parameters, CancellationToken cancellationToken)
    {
        // The node to expand is identified by an opaque token the client echoes
        // back verbatim (see ExpandToken). A missing token means "the workspace
        // root", i.e. the top level of the tree.
        string? tokenText = null;
        if (parameters.TryGetProperty("expandToken", out var tokenElement)
            && tokenElement.ValueKind == JsonValueKind.String)
        {
            tokenText = tokenElement.GetString();
        }
        ExpandToken.Payload? token = ExpandToken.Decode(tokenText);

        InMemoryProject project = _core.GetOrCreateProject("default");

        // explicit rescan request (the extension's refresh command). Keeps the
        // tree in sync with files added / removed on disk after initialize.
        if (parameters.TryGetProperty("rescan", out var rescanElement) && rescanElement.ValueKind == JsonValueKind.True)
        {
            RescanWorkspaceFiles(project);
        }

        // Self-heal the top level: when the client never reported a workspace
        // root (single file opened, "File > Open File"), the scan at initialize
        // never ran and the tree would only ever list the open documents. The
        // directory of any known document is a good enough root to scan once so
        // the whole project appears.
        if (token == null && _workspaceRoots.Count == 0)
        {
            EnsureWorkspaceRootFromKnownFiles(project);
        }

        // A single unreadable directory / file must never blank the whole tree:
        // the request is answered with whatever could be listed.
        try
        {
            switch (token?.Kind)
            {
                case ExpandToken.TokenKind.Folder:
                    return new HierarchyResult { Nodes = BuildFolderNodes(project, token.FolderPath ?? string.Empty) };

                case ExpandToken.TokenKind.File:
                {
                    string fileUri = NormalizeUri(token.Uri ?? string.Empty);
                    InMemoryFile? file = TryLoadFileFromDisk(project, fileUri);
                    if (file == null) return new HierarchyResult { Nodes = new List<HierarchyNode>() };
                    return new HierarchyResult { Nodes = BuildFileNodes(file, Array.Empty<string>()) };
                }

                case ExpandToken.TokenKind.Instance:
                {
                    string fileUri = NormalizeUri(token.Uri ?? string.Empty);
                    InMemoryFile? file = TryLoadFileFromDisk(project, fileUri);
                    if (file == null) return new HierarchyResult { Nodes = new List<HierarchyNode>() };
                    return new HierarchyResult
                    {
                        Nodes = BuildFileNodes(file, token.InstancePath ?? Array.Empty<string>()),
                    };
                }

                default:
                    // no token (or an unknown one): the workspace top level
                    return new HierarchyResult { Nodes = BuildWorkspaceNodes(project) };
            }
        }
        catch (Exception ex)
        {
            // Any failure while building the tree must not blank the view: the
            // client renders an empty tree when the request errors out, which
            // looks like "the extension does not start". Log the full stack so
            // the cause is visible in the extension log, and answer with an
            // empty (but valid) result.
            Console.Error.WriteLine($"rtl/hierarchy failed: {ex}");
            return new HierarchyResult { Nodes = new List<HierarchyNode>() };
        }
    }

    /// <summary>
    /// Top level of the hierarchy view: the whole workspace, i.e. every
    /// SystemVerilog / Verilog file loaded from the workspace roots (not just
    /// the documents the client opened). Folders come first, files after, both
    /// sorted by name so the tree mirrors the on-disk project layout.
    /// </summary>
    private List<HierarchyNode> BuildWorkspaceNodes(InMemoryProject project)
    {
        return BuildFolderNodes(project, string.Empty);
    }

    /// <summary>
    /// Children of one workspace folder: its sub-folders followed by the
    /// SystemVerilog / Verilog files directly inside it. An empty
    /// <paramref name="folderPath"/> lists the workspace roots themselves, so
    /// the top level of the tree is the workspace as a whole.
    /// </summary>
    private List<HierarchyNode> BuildFolderNodes(InMemoryProject project, string folderPath)
    {
        // Top level: mirror the project layout the way RtlEditor2's NavigateView
        // does. With a single workspace root its children are listed directly;
        // with several roots each root becomes a folder node.
        if (string.IsNullOrEmpty(folderPath))
        {
            if (project.WorkspaceRoots.Count == 0) return new List<HierarchyNode>();
            if (project.WorkspaceRoots.Count == 1)
            {
                return BuildDirectoryNodes(project, project.WorkspaceRoots[0], string.Empty);
            }

            List<HierarchyNode> roots = new();
            foreach (string root in project.WorkspaceRoots)
            {
                string trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                roots.Add(new HierarchyNode
                {
                    Label = Path.GetFileName(trimmed),
                    Kind = HierarchyNodeKind.Folder,
                    FolderPath = root,
                    HasChildren = true,
                    Icon = "folder",
                    ContextValue = "rtlFolder",
                    Collapsible = "collapsed",
                    Tooltip = root,
                    FsPath = root,
                    DirPath = root,
                    ExpandToken = ExpandToken.ForFolder(root),
                });
            }
            roots.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return roots;
        }

        string? directory = ResolveFolderDirectory(project, folderPath);
        if (directory == null) return new List<HierarchyNode>();

        // a relative folderPath is resolved against the single workspace root,
        // so its children stay relative; an absolute one (multi-root listing)
        // keeps absolute child paths
        string basePath = Path.IsPathRooted(folderPath) ? folderPath : folderPath;
        return BuildDirectoryNodes(project, directory, basePath);
    }

    /// <summary>
    /// Maps a <c>folderPath</c> from the tree back to an absolute directory.
    /// Accepts both a workspace-relative path (single root) and an absolute
    /// path (a root node when several roots are open).
    /// </summary>
    private InMemoryFile? TryLoadFileFromDisk(InMemoryProject project, string uri)
    {
        if (project.FindFile(uri) is InMemoryFile known) return known;

        string? path = UriToAbsolutePath(uri);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        string ext = Path.GetExtension(path);
        if (ext is not (".sv" or ".svh" or ".v" or ".vh")) return null;

        try
        {
            string text = File.ReadAllText(path);
            bool isSystemVerilog = ext is ".sv" or ".svh";
            InMemoryFile file = project.AddOrUpdateFile(uri, path, text, isSystemVerilog);
            file.ForceBuild();
            return file;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ResolveFolderDirectory(InMemoryProject project, string folderPath)
    {
        if (Path.IsPathRooted(folderPath))
        {
            return Directory.Exists(folderPath) ? folderPath : null;
        }

        foreach (string root in project.WorkspaceRoots)
        {
            string candidate = Path.Combine(root, folderPath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Lists the real contents of <paramref name="directory"/>: every
    /// sub-directory and every file, exactly like RtlEditor2's NavigateView
    /// (not only the SystemVerilog sources). Build output / VCS directories are
    /// skipped. A SystemVerilog / Verilog file that the server has parsed is
    /// expandable into its instance hierarchy; other files are leaves that open
    /// in the editor.
    /// </summary>
    private List<HierarchyNode> BuildDirectoryNodes(InMemoryProject project, string directory, string basePath)
    {
        List<HierarchyNode> nodes = new();

        // Materialise the listings inside the try: Directory.Enumerate* is lazy,
        // so an unreadable entry would otherwise throw from the foreach below,
        // outside the guard, and the whole rtl/hierarchy request would fail
        // (the tree then renders empty).
        List<string> subDirectories;
        List<string> files;
        try
        {
            subDirectories = Directory.EnumerateDirectories(directory).ToList();
            files = Directory.EnumerateFiles(directory).ToList();
        }
        catch (IOException)
        {
            return nodes;
        }
        catch (UnauthorizedAccessException)
        {
            return nodes;
        }

        foreach (string sub in subDirectories)
        {
            string name = Path.GetFileName(sub);
            if (SkippedDirectoryNames.Contains(name)) continue;

            string childFolderPath = string.IsNullOrEmpty(basePath) ? name : basePath + "/" + name;
            nodes.Add(new HierarchyNode
            {
                Label = name,
                Kind = HierarchyNodeKind.Folder,
                FolderPath = childFolderPath,
                HasChildren = true,
                Icon = "folder",
                ContextValue = "rtlFolder",
                Collapsible = "collapsed",
                Tooltip = childFolderPath,
                FsPath = sub,
                DirPath = sub,
                ExpandToken = ExpandToken.ForFolder(childFolderPath),
            });
        }

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            string uri = PathToUri(path);

            // A SystemVerilog / Verilog source is expandable into its instance
            // hierarchy; it is loaded and parsed lazily on expansion, so an
            // unparsed file still has to advertise itself as expandable or the
            // user could never expand it (the tree would show leaves only).
            string ext = Path.GetExtension(path);
            bool hasChildren = project.FindFile(uri) is InMemoryFile known
                ? known.HasHierarchyChildren()
                : ext is ".sv" or ".svh" or ".v" or ".vh";

            // Flag files with errors / warnings the way the editor's
            // NavigatePanel does with its exclamation badge. Only already
            // parsed files are inspected: parsing every file in the listing
            // would defeat the lazy expansion. HighestDiagnosticSeverity takes
            // the file's own lock, so a background parse replacing the list
            // cannot break the enumeration (which would fail the whole
            // rtl/hierarchy request and blank the tree).
            string? iconColor = null;
            if (project.FindFile(uri) is InMemoryFile parsedFile)
            {
                iconColor = parsedFile.HighestDiagnosticSeverity() switch
                {
                    SystemVerilogSeverity.Error => "errorForeground",
                    SystemVerilogSeverity.Warning => "editorWarning.foreground",
                    _ => null,
                };
            }

            // The icon name mirrors the editor's NavigatePanel: a SystemVerilog
            // document, a Verilog document or a header, chosen by extension.
            // The client maps it onto the plugin's own SVG assets.
            string iconName = ext switch
            {
                ".sv" => "systemverilog-document",
                ".svh" => "verilog-header-document",
                ".v" => "verilog-document",
                ".vh" => "verilog-header-document",
                _ => "file",
            };

            nodes.Add(new HierarchyNode
            {
                Label = name,
                Uri = uri,
                Kind = HierarchyNodeKind.File,
                Detail = GetRelativePath(project, path) ?? name,
                HasChildren = hasChildren,
                Icon = iconName,
                IconColor = iconColor,
                ContextValue = "rtlFile",
                Collapsible = hasChildren ? "collapsed" : "none",
                Tooltip = uri,
                FsPath = path,
                DirPath = Path.GetDirectoryName(path),
                ExpandToken = hasChildren ? ExpandToken.ForFile(uri) : null,
            });
        }

        nodes.Sort((a, b) =>
        {
            // folders first, then files, each alphabetically
            bool aFolder = a.Kind == HierarchyNodeKind.Folder;
            bool bFolder = b.Kind == HierarchyNodeKind.Folder;
            if (aFolder != bFolder) return aFolder ? -1 : 1;
            return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
        return nodes;
    }

    /// <summary>
    /// Directory names whose contents are build output / VCS metadata and are
    /// never shown in the tree.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        { "obj", "bin", "publish", "node_modules", ".git", ".svn", ".hg" };

    /// <summary>
    /// Registers the single folder name that <paramref name="relativePath"/>
    /// contributes to the listing of <paramref name="folderPath"/>: the path
    /// segment directly below <paramref name="folderPath"/> on this file's
    /// branch (so "src/rtl/x.sv" contributes "rtl" to the listing of "src", and
    /// "src" to the listing of the workspace root).
    ///
    /// <para>
    /// Files that do not live under <paramref name="folderPath"/> contribute
    /// nothing: walking up without this prefix check made a file in
    /// "src/rtl" advertise a bogus "src" child when the expanded folder was
    /// "src/tb".
    /// </para>
    /// </summary>
    private static void AddChildFolder(string relativePath, string folderPath, SortedSet<string> folders)
    {
        string prefix = string.IsNullOrEmpty(folderPath) ? string.Empty : folderPath + "/";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        string remainder = relativePath.Substring(prefix.Length);
        int slash = remainder.IndexOf('/');
        if (slash <= 0) return;   // the file sits directly in folderPath: no sub-folder

        folders.Add(remainder.Substring(0, slash));
    }

    /// <summary>
    /// Parent folder of a workspace-relative path in '/'-separated form, or
    /// null when the path is directly under the workspace root.
    /// </summary>
    private static string? GetParentFolder(string relativePath)
    {
        int index = relativePath.LastIndexOf('/');
        return index <= 0 ? null : relativePath.Substring(0, index);
    }

    /// <summary>
    /// Children of the node identified by <paramref name="nodePath"/> in the
    /// given file. Works purely on the lazily built symbol list so the tree does
    /// not have to be materialised ahead of time.
    /// </summary>
    private static List<HierarchyNode> BuildFileNodes(InMemoryFile file, IReadOnlyList<string> instancePath)
    {
        // The editor's NavigatePanel update code resolves the children of the
        // node being expanded (an empty path means "the first level of this
        // file"), so the tree is built lazily exactly like the editor's.
        List<HierarchyNode> result = new();
        foreach (HierarchyNodeDescriptor child in file.GetHierarchyChildren(instancePath))
        {
            result.Add(new HierarchyNode
            {
                Label = child.Label,
                Kind = HierarchyNodeKind.Instance,
                Detail = child.Detail,
                Uri = child.Uri,
                StartLine = child.StartLine,
                StartCharacter = child.StartCharacter,
                HasChildren = child.HasChildren,
                // presentation metadata: the client only maps it onto the
                // VS Code TreeItem (no derivation on the client side). The
                // icon comes from the descriptor (module / interface / package
                // / include header) so the tree distinguishes them, and the
                // colour flags files with errors / warnings.
                Icon = child.Icon ?? "module",
                IconColor = child.IconColor,
                ContextValue = "rtlInstance",
                Collapsible = child.HasChildren ? "collapsed" : "none",
                Tooltip = string.IsNullOrEmpty(child.Detail)
                    ? child.InstancePath
                    : $"{child.InstancePath}  ({child.Detail})",
                InstancePath = child.InstancePath,
                // the client echoes this back in rtl/reparse so the server can
                // parse the document AS this instance (parameter overrides
                // applied), matching what the editor shows when the node is
                // selected
                InstancePathSegments = child.InstancePathSegments.Count > 0
                    ? new List<string>(child.InstancePathSegments)
                    : null,
                ParentUri = child.ParentUri,
                FsPath = child.FsPath,
                DirPath = child.DirPath,
                // opaque token for the next expansion (null for leaves). The
                // token addresses the instance inside the file its
                // instantiation is WRITTEN IN (ExpandUri), which is not the
                // file the node opens on click (Uri = the instantiated
                // module's own source).
                ExpandToken = child.HasChildren && !string.IsNullOrEmpty(child.ExpandUri)
                    ? ExpandToken.ForInstance(child.ExpandUri!, child.InstancePathSegments)
                    : null,
            });
        }
        return result;
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

    /// <summary>
    /// Pushes <c>rtl/hierarchyInvalidated</c> so the client redraws the RTL
    /// Hierarchy tree without waiting for the user to press refresh.
    ///
    /// <para>
    /// This is a non-standard notification (vendor prefix <c>rtl/</c>, like
    /// <c>rtl/hierarchy</c> / <c>rtl/reparse</c>), so the standard LSP surface
    /// is untouched. The client only has to redraw: the tree is fetched lazily
    /// per expanded node, so no payload is needed here.
    /// </para>
    ///
    /// <para>
    /// Called from the message loop (didSave / watched files / workspace folder
    /// changes), so the writer is only ever used from the single LSP loop and
    /// no interleaving can occur. A no-op until the host injects
    /// <see cref="NotificationSender"/>.
    /// </para>
    /// </summary>
    private void PushHierarchyInvalidated(string reason)
    {
        Action<LspMessage>? sender = NotificationSender;
        if (sender == null) return;

        sender(new LspMessage
        {
            Method = "rtl/hierarchyInvalidated",
            Params = new HierarchyInvalidatedParams { Reason = reason },
        });
    }

    // ---------------------- helpers ----------------------

    private bool TryGetFileAndIndex(JsonElement parameters, out ISystemVerilogFile file, out int index)
    {
        file = null!;
        index = 0;
        if (!parameters.TryGetProperty("textDocument", out var td)) return false;
        if (!td.TryGetProperty("uri", out var uriProp)) return false;
        string uri = NormalizeUri(uriProp.GetString() ?? string.Empty);
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

    // ---- standard LSP features wired but not implemented yet ----
    [JsonPropertyName("documentFormattingProvider")] public bool DocumentFormattingProvider { get; set; }
    [JsonPropertyName("documentRangeFormattingProvider")] public bool DocumentRangeFormattingProvider { get; set; }
    [JsonPropertyName("renameProvider")] public bool RenameProvider { get; set; }
    [JsonPropertyName("codeActionProvider")] public bool CodeActionProvider { get; set; }
    [JsonPropertyName("codeLensProvider")] public CodeLensCapability? CodeLensProvider { get; set; }
    [JsonPropertyName("signatureHelpProvider")] public SignatureHelpCapability? SignatureHelpProvider { get; set; }
    [JsonPropertyName("inlayHintProvider")] public bool InlayHintProvider { get; set; }
    [JsonPropertyName("declarationProvider")] public bool DeclarationProvider { get; set; }
    [JsonPropertyName("typeDefinitionProvider")] public bool TypeDefinitionProvider { get; set; }
    [JsonPropertyName("implementationProvider")] public bool ImplementationProvider { get; set; }
    [JsonPropertyName("documentHighlightProvider")] public bool DocumentHighlightProvider { get; set; }
    [JsonPropertyName("foldingRangeProvider")] public bool FoldingRangeProvider { get; set; }
    [JsonPropertyName("selectionRangeProvider")] public bool SelectionRangeProvider { get; set; }
    [JsonPropertyName("documentLinkProvider")] public DocumentLinkCapability? DocumentLinkProvider { get; set; }
    [JsonPropertyName("workspaceSymbolProvider")] public bool WorkspaceSymbolProvider { get; set; }
    [JsonPropertyName("executeCommandProvider")] public ExecuteCommandCapability? ExecuteCommandProvider { get; set; }
}

public sealed class CodeLensCapability
{
    [JsonPropertyName("resolveProvider")] public bool ResolveProvider { get; set; }
}

public sealed class SignatureHelpCapability
{
    [JsonPropertyName("triggerCharacters")] public List<string>? TriggerCharacters { get; set; }
    [JsonPropertyName("retriggerCharacters")] public List<string>? RetriggerCharacters { get; set; }
}

public sealed class DocumentLinkCapability
{
    [JsonPropertyName("resolveProvider")] public bool ResolveProvider { get; set; }
}

public sealed class ExecuteCommandCapability
{
    [JsonPropertyName("commands")] public List<string> Commands { get; set; } = new();
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
    /// <summary>Text inserted on accept. Omitted when it equals the label.</summary>
    [JsonPropertyName("insertText")] public string? InsertText { get; set; }
    /// <summary>LSP InsertTextFormat (1=PlainText, 2=Snippet).</summary>
    [JsonPropertyName("insertTextFormat")] public int? InsertTextFormat { get; set; }
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

/// <summary>
/// Body of the <c>rtl/hierarchyInvalidated</c> notification. The client only
/// needs to redraw the tree, so <see cref="Reason"/> is informational (it shows
/// up in the extension log).
/// </summary>
public sealed class HierarchyInvalidatedParams
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>
/// Body of the <c>rtl/theme</c> request: the semantic-token colour rules
/// (token type → "#RRGGBB") plus the legend the client must use.
/// </summary>
public sealed class ThemeResult
{
    [JsonPropertyName("rules")] public Dictionary<string, string> Rules { get; set; } = new();
    [JsonPropertyName("tokenTypes")] public List<string> TokenTypes { get; set; } = new();
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
    [JsonPropertyName("range")] public bool Range { get; set; }
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

// ---------------------- DTOs for the not-yet-implemented features ----------------------
// The handlers in LspHandler.Unimplemented.cs return empty collections of these
// types, so the wire shape is already correct when the implementation lands.

public sealed class TextEdit
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("newText")] public string NewText { get; set; } = string.Empty;
}

public sealed class CodeAction
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("diagnostics")] public List<Diagnostic>? Diagnostics { get; set; }
    [JsonPropertyName("edit")] public WorkspaceEdit? Edit { get; set; }
}

public sealed class WorkspaceEdit
{
    [JsonPropertyName("changes")] public Dictionary<string, List<TextEdit>>? Changes { get; set; }
}

public sealed class PrepareRenameResult
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("placeholder")] public string? Placeholder { get; set; }
}

public sealed class CodeLens
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("command")] public Command? Command { get; set; }
}

public sealed class Command
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("command")] public string CommandName { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public List<object>? Arguments { get; set; }
}

public sealed class SignatureHelp
{
    [JsonPropertyName("signatures")] public List<SignatureInformation> Signatures { get; set; } = new();
    [JsonPropertyName("activeSignature")] public int ActiveSignature { get; set; }
    [JsonPropertyName("activeParameter")] public int ActiveParameter { get; set; }
}

public sealed class SignatureInformation
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("documentation")] public MarkupContent? Documentation { get; set; }
    [JsonPropertyName("parameters")] public List<ParameterInformation>? Parameters { get; set; }
}

public sealed class ParameterInformation
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
}

public sealed class InlayHint
{
    [JsonPropertyName("position")] public LspPosition Position { get; set; } = new();
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public int? Kind { get; set; }
}

public sealed class DocumentHighlight
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("kind")] public int? Kind { get; set; }
}

public sealed class FoldingRange
{
    [JsonPropertyName("startLine")] public int StartLine { get; set; }
    [JsonPropertyName("endLine")] public int EndLine { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

public sealed class SelectionRange
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("parent")] public SelectionRange? Parent { get; set; }
}

public sealed class DocumentLink
{
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("target")] public string? Target { get; set; }
}

public sealed class WorkspaceSymbol
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public SymbolKind Kind { get; set; }
    [JsonPropertyName("location")] public Location Location { get; set; } = new();
}
