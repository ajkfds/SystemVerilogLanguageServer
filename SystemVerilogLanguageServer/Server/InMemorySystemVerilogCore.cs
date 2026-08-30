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
        _files[id] = file;
        return file;
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
        if (file is InMemoryFile mem) return mem.FindDefinition(index);
        return null;
    }

    public IReadOnlyList<ISystemVerilogNamedElement> FindReferences(ISystemVerilogFile file, int index)
    {
        if (file is InMemoryFile mem) return mem.FindReferences(index);
        return System.Array.Empty<ISystemVerilogNamedElement>();
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
    private readonly List<ISystemVerilogNamedElement> _symbols = new();

    public InMemoryFile(string id, string? absolutePath, bool isSystemVerilog, string text)
    {
        Id = id;
        AbsolutePath = absolutePath;
        IsSystemVerilog = isSystemVerilog;
        CodeDocument = new InMemoryCodeDocument(text);
    }

    public string Id { get; }
    public string? AbsolutePath { get; }
    public bool IsSystemVerilog { get; }
    public ISystemVerilogCodeDocument CodeDocument { get; }

    public IReadOnlyList<ISystemVerilogBuildingBlock> TopLevelBlocks => System.Array.Empty<ISystemVerilogBuildingBlock>();

    public void AddSymbol(ISystemVerilogNamedElement symbol) => _symbols.Add(symbol);

    public ISystemVerilogDocument GetOrCreateDocument() => _document ??= new InMemoryDocument(this);

    public ISystemVerilogNamedElement? FindDefinition(int index)
    {
        foreach (ISystemVerilogNamedElement symbol in _symbols)
        {
            if (symbol.DefinitionRange is { } r && r.Contains(index))
            {
                return symbol;
            }
        }
        return null;
    }

    public IReadOnlyList<ISystemVerilogNamedElement> FindReferences(int index)
    {
        ISystemVerilogNamedElement? def = FindDefinition(index);
        if (def == null) return System.Array.Empty<ISystemVerilogNamedElement>();
        return new[] { def };
    }
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
    public IReadOnlyList<ISystemVerilogDiagnostic> Diagnostics { get; } = System.Array.Empty<ISystemVerilogDiagnostic>();

    public ISystemVerilogNamedElement? FindElementAt(int index) => null;
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
    public IReadOnlyDictionary<string, ISystemVerilogBuildingBlock> BuildingBlocks { get; } = new Dictionary<string, ISystemVerilogBuildingBlock>();
    public IReadOnlyList<ISystemVerilogNamedElement> Members { get; } = System.Array.Empty<ISystemVerilogNamedElement>();
    public ISystemVerilogBuildingBlock? Owner => null;
    public ISystemVerilogFile? File { get; }

    SystemVerilogNamedElementKind ISystemVerilogNamedElement.Kind => SystemVerilogNamedElementKind.Unknown;
}
