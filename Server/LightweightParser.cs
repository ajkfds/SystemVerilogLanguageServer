using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// Lightweight token-based scanner that extracts the declarations and
/// references needed by the language server (definitions, hover, outline,
/// references) without pulling in the full plugin parser. Not a complete
/// SystemVerilog parser: it recognises the structural constructs that map
/// to LSP features.
/// </summary>
public static class LightweightParser
{
    /// <summary>Parses the given source text and returns the extracted symbols.</summary>
    public static ParseResult Parse(string text)
    {
        var result = new ParseResult();
        if (string.IsNullOrEmpty(text)) return result;

        int n = text.Length;
        int i = 0;
        var blockStack = new List<Scope>(); // open building blocks, last = innermost
        var pendingLabel = (string?)null; // "name :" label before a construct

        while (i < n)
        {
            char c = text[i];

            // --- comments ---
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int start = i;
                while (i < n && text[i] != '\n') i++;
                result.AddToken(TokenTypes.Comment, start, i);
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                result.AddToken(TokenTypes.Comment, start, i);
                continue;
            }
            // --- strings ---
            if (c == '"')
            {
                int start = i;
                i++;
                while (i < n && text[i] != '"')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                i = Math.Min(i + 1, n);
                result.AddToken(TokenTypes.String, start, i);
                continue;
            }
            // --- macros (`NAME / `define) ---
            if (c == '`' && i + 1 < n && IsIdentStart(text[i + 1]))
            {
                int start = i;
                i++;
                while (i < n && IsIdentChar(text[i])) i++;
                result.AddToken(TokenTypes.Macro, start, i);
                continue;
            }
            // --- system tasks / functions ($display) ---
            if (c == '$' && i + 1 < n && IsIdentStart(text[i + 1]))
            {
                int start = i;
                i++;
                while (i < n && IsIdentChar(text[i])) i++;
                result.AddToken(TokenTypes.Function, start, i);
                continue;
            }
            // --- numbers (incl. sized literals like 8'hFF) ---
            if (char.IsDigit(c))
            {
                int start = i;
                i++;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '\'' || text[i] == '.' || text[i] == '?' || text[i] == 'x' || text[i] == 'X' || text[i] == 'z' || text[i] == 'Z'))
                {
                    // stop a based literal at the first whitespace after the base part
                    if (text[i] == '\'')
                    {
                        i++;
                        while (i < n && IsIdentChar(text[i])) i++;
                        break;
                    }
                    i++;
                }
                result.AddToken(TokenTypes.Number, start, i);
                continue;
            }

            if (!IsIdentStart(c)) { i++; continue; }

            int wordStart = i;
            while (i < n && IsIdentChar(text[i])) i++;
            string word = text.Substring(wordStart, i - wordStart);

            if (IsKeywordWord(word))
            {
                result.AddToken(TokenTypes.Keyword, wordStart, i);
            }

            switch (word)
            {
                case "module": case "macromodule":
                    BeginBlock(result, blockStack, ScopeKind.Module, text, i, n, ref pendingLabel);
                    break;
                case "interface":
                    BeginBlock(result, blockStack, ScopeKind.Interface, text, i, n, ref pendingLabel);
                    break;
                case "package":
                    BeginBlock(result, blockStack, ScopeKind.Package, text, i, n, ref pendingLabel);
                    break;
                case "program":
                    BeginBlock(result, blockStack, ScopeKind.Program, text, i, n, ref pendingLabel);
                    break;
                case "checker":
                    BeginBlock(result, blockStack, ScopeKind.Checker, text, i, n, ref pendingLabel);
                    break;
                case "primitive":
                    BeginBlock(result, blockStack, ScopeKind.Primitive, text, i, n, ref pendingLabel);
                    break;
                case "class":
                    BeginBlock(result, blockStack, ScopeKind.Class, text, i, n, ref pendingLabel);
                    break;
                case "function":
                    BeginMember(result, blockStack, MemberKind.Function, text, i, n, ref pendingLabel);
                    break;
                case "task":
                    BeginMember(result, blockStack, MemberKind.Task, text, i, n, ref pendingLabel);
                    break;

                case "endmodule": case "endinterface": case "endpackage":
                case "endprogram": case "endchecker": case "endprimitive": case "endclass":
                    EndBlock(blockStack, text, i, n, ref pendingLabel);
                    break;

                case "endfunction": case "endtask":
                    EndMember(blockStack, ref pendingLabel);
                    break;

                case "typedef":
                    ParseTypedef(result, blockStack, text, i, n);
                    break;

                case "endcase": case "join": case "join_any": case "join_none":
                case "begin": case "fork":
                    // consume optional "begin : label" / closing "end : label"
                    SkipLabelAfterColon(text, i, n, ref pendingLabel);
                    break;

                default:
                    if (!IsKeywordWord(word))
                    {
                        result.AddToken(TokenTypes.Identifier, wordStart, i);
                    }
                    ParseDeclarationWord(result, blockStack, word, text, wordStart, i, n, ref pendingLabel);
                    break;
            }
        }

        // declaration symbols override the plain identifier colour
        foreach (SymbolInfo sym in result.Symbols)
        {
            TokenTypes t = sym.Kind switch
            {
                SystemVerilogNamedElementKindLocal.Module or
                SystemVerilogNamedElementKindLocal.Interface or
                SystemVerilogNamedElementKindLocal.Package or
                SystemVerilogNamedElementKindLocal.Program or
                SystemVerilogNamedElementKindLocal.Checker or
                SystemVerilogNamedElementKindLocal.Primitive or
                SystemVerilogNamedElementKindLocal.Class or
                SystemVerilogNamedElementKindLocal.Function or
                SystemVerilogNamedElementKindLocal.Task or
                SystemVerilogNamedElementKindLocal.Typedef
                    => TokenTypes.Type,
                SystemVerilogNamedElementKindLocal.Parameter or
                SystemVerilogNamedElementKindLocal.LocalParameter
                    => TokenTypes.Parameter,
                SystemVerilogNamedElementKindLocal.Net => TokenTypes.Property,
                SystemVerilogNamedElementKindLocal.Variable => TokenTypes.Variable,
                SystemVerilogNamedElementKindLocal.Port => TokenTypes.Variable,
                _ => TokenTypes.Identifier,
            };
            result.AddToken(t, sym.Start, sym.End);
        }
        // reg 型の宣言は Register 色 (Variable の上書きは不要 — 便宜的に同色)
        return result;
    }

    private static bool IsKeywordWord(string w)
    {
        switch (w)
        {
            case "module": case "macromodule": case "endmodule":
            case "interface": case "endinterface":
            case "package": case "endpackage":
            case "program": case "endprogram":
            case "checker": case "endchecker":
            case "primitive": case "endprimitive":
            case "config": case "endconfig":
            case "class": case "endclass": case "extends": case "implements": case "virtual":
            case "function": case "endfunction":
            case "task": case "endtask":
            case "typedef": case "enum": case "struct": case "union": case "packed": case "tagged":
            case "if": case "else": case "case": case "casex": case "casez": case "endcase": case "default":
            case "for": case "foreach": case "forever": case "repeat": case "while": case "do":
            case "break": case "continue": case "return":
            case "begin": case "end": case "fork": case "join": case "join_any": case "join_none":
            case "wait": case "wait_order": case "disable":
            case "assert": case "assume": case "cover": case "expect":
            case "property": case "endproperty": case "sequence": case "endsequence":
            case "clocking": case "endclocking":
            case "always": case "always_comb": case "always_ff": case "always_latch":
            case "initial": case "final": case "assign": case "force": case "release": case "deassign": case "defparam":
            case "generate": case "endgenerate": case "genvar":
            case "bind": case "alias": case "import": case "export": case "extern": case "pure": case "context":
            case "local": case "static": case "automatic":
            case "input": case "output": case "inout": case "ref":
            case "posedge": case "negedge": case "edge": case "iff":
            case "with": case "inside": case "dist": case "unique": case "priority":
            case "rand": case "randc": case "constraint": case "solve": case "before": case "cross":
            case "coverpoint": case "covergroup": case "endgroup": case "sample":
            case "randcase": case "randsequence":
            case "new": case "null": case "this": case "super":
            case "timeunit": case "timeprecision": case "timescale":
            case "cell": case "use": case "liblist": case "instance": case "design": case "incdir": case "include": case "option": case "library":
            case "nettype": case "interconnect": case "soft": case "type":
            case "parameter": case "localparam": case "specparam":
            case "var": case "const": case "signed": case "unsigned":
            case "wire": case "wand": case "wor": case "tri": case "tri0": case "tri1": case "uwire":
            case "supply0": case "supply1": case "scalared": case "vectored":
            case "small": case "medium": case "large": case "strength":
                return true;
        }
        return false;
    }

    // ---------------- structural helpers ----------------

    private static void BeginBlock(ParseResult result, List<Scope> stack, ScopeKind kind,
        string text, int afterKeyword, int n, ref string? pendingLabel)
    {
        // keyword [automatic|static] [lifetime] name [#(...)] [(ports)] ;  or  "end X : name"
        int i = SkipWsComments(text, afterKeyword, n);
        // "endmodule : name" style is handled by EndBlock; here we expect a name
        if (i < n && IsIdentStart(text[i]))
        {
            int nameStart = i;
            while (i < n && IsIdentChar(text[i])) i++;
            string name = text.Substring(nameStart, i - nameStart);
            var parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
            var scope = new Scope { Kind = kind, Name = name, NameStart = nameStart, Label = pendingLabel, Owner = parent };
            pendingLabel = null;
            stack.Add(scope);
            result.Scopes.Add(scope);
            result.Symbols.Add(new SymbolInfo
            {
                Name = name,
                Kind = KindToNamedElementKind(kind),
                Start = nameStart,
                End = i,
                Owner = stack.Count >= 2 ? stack[stack.Count - 2] : null,
                IsDeclaration = true,
            });
        }
        else
        {
            pendingLabel = null;
        }
    }

    private static void EndBlock(List<Scope> stack, string text, int atKeyword, int n, ref string? pendingLabel)
    {
        pendingLabel = null;
        // "endmodule : name" trailing label: skip it so it is not treated as a reference
        int i = SkipWsComments(text, atKeyword, n);
        if (i < n && text[i] == ':')
        {
            i = SkipWsComments(text, i + 1, n);
            while (i < n && IsIdentChar(text[i])) i++;
        }
    }

    private static void BeginMember(ParseResult result, List<Scope> stack, MemberKind kind,
        string text, int afterKeyword, int n, ref string? pendingLabel)
    {
        // function [automatic] [return_type] name ;  /  task [automatic] name ;
        int i = SkipWsComments(text, afterKeyword, n);
        string name = "";
        int nameStart = -1;
        int nameEnd = -1;
        // skip optional qualifiers / return type words until an identifier that is not a type keyword
        while (i < n && IsIdentStart(text[i]))
        {
            int ws = i;
            while (i < n && IsIdentChar(text[i])) i++;
            string w = text.Substring(ws, i - ws);
            if (IsTypeWord(w) || w == "automatic" || w == "static" || w == "context" || w == "pure")
            {
                i = SkipWsComments(text, i, n);
                continue;
            }
            name = w;
            nameStart = ws;
            nameEnd = i;
            break;
        }
        if (nameStart >= 0)
        {
            result.Symbols.Add(new SymbolInfo
            {
                Name = name,
                Kind = kind == MemberKind.Function ? SystemVerilogNamedElementKindLocal.Function : SystemVerilogNamedElementKindLocal.Task,
                Start = nameStart,
                End = nameEnd,
                Owner = stack.Count > 0 ? stack[stack.Count - 1] : null,
                IsDeclaration = true,
            });
        }
        pendingLabel = null;
    }

    private static void EndMember(List<Scope> stack, ref string? pendingLabel)
    {
        pendingLabel = null;
    }

    private static void ParseTypedef(ParseResult result, List<Scope> stack, string text, int afterKeyword, int n)
    {
        // typedef [class|interface class|struct|union|enum|type] name ;
        int i = SkipWsComments(text, afterKeyword, n);
        // skip "class", "interface class", "struct", "union", "enum", "struct packed", type words, dimensions
        while (i < n && IsIdentStart(text[i]))
        {
            int ws = i;
            while (i < n && IsIdentChar(text[i])) i++;
            string w = text.Substring(ws, i - ws);
            bool structural = w == "class" || w == "interface" || w == "struct" || w == "union" || w == "enum" || IsTypeWord(w);
            if (structural)
            {
                i = SkipWsComments(text, i, n);
                // handle "interface class"
                if (w == "interface" && i < n && IsIdentStart(text[i]))
                {
                    int ws2 = i;
                    while (i < n && IsIdentChar(text[i])) i++;
                    string w2 = text.Substring(ws2, i - ws2);
                    if (w2 == "class") i = SkipWsComments(text, i, n);
                }
                continue;
            }
            // first non-type identifier is the typedef name
            result.Symbols.Add(new SymbolInfo
            {
                Name = w,
                Kind = SystemVerilogNamedElementKindLocal.Typedef,
                Start = ws,
                End = i,
                Owner = stack.Count > 0 ? stack[stack.Count - 1] : null,
                IsDeclaration = true,
            });
            return;
        }
    }

    private static void ParseDeclarationWord(ParseResult result, List<Scope> stack, string word,
        string text, int wordStart, int wordEnd, int n, ref string? pendingLabel)
    {
        int i = wordEnd;

        // "name :" label candidate (statement label / block label)
        if (pendingLabel == null && word != "begin" && word != "fork")
        {
            int j = SkipWsComments(text, i, n);
            if (j < n && text[j] == ':')
            {
                pendingLabel = word;
                return;
            }
        }
        pendingLabel = null;

        // reference collection (ports / instantiations resolve later by name)
        result.References.Add(new ReferenceInfo { Name = word, Start = wordStart, End = wordEnd });

        // declarations: <type-words...> name [unpacked dims] (= expr)? ;
        // recognised when followed (after optional dims) by another identifier or ','
        if (!IsDeclarationTypeWord(word)) return;

        i = SkipWsComments(text, i, n);
        // skip packed range(s) [..] and signed/unsigned
        while (i < n && (text[i] == '[' || IsIdentStart(text[i])))
        {
            if (text[i] == '[')
            {
                i = SkipBracket(text, i, n);
                i = SkipWsComments(text, i, n);
                continue;
            }
            int ws = i;
            while (i < n && IsIdentChar(text[i])) i++;
            string w = text.Substring(ws, i - ws);
            if (w == "signed" || w == "unsigned") { i = SkipWsComments(text, i, n); continue; }
            // the type name itself (e.g. my_type_t) — treat as type, keep scanning
            i = SkipWsComments(text, i, n);
            if (i < n && text[i] == '[') continue;
            break;
        }
        if (i < n && IsIdentStart(text[i]))
        {
            int nameStart = i;
            while (i < n && IsIdentChar(text[i])) i++;
            string name = text.Substring(nameStart, i - nameStart);

            SystemVerilogNamedElementKindLocal kind = word switch
            {
                "parameter" => SystemVerilogNamedElementKindLocal.Parameter,
                "localparam" => SystemVerilogNamedElementKindLocal.LocalParameter,
                "wire" or "wand" or "wor" or "tri" or "tri0" or "tri1" or "supply0" or "supply1" or "uwire"
                    => SystemVerilogNamedElementKindLocal.Net,
                "input" or "output" or "inout" or "ref" => SystemVerilogNamedElementKindLocal.Port,
                _ => SystemVerilogNamedElementKindLocal.Variable,
            };

            result.Symbols.Add(new SymbolInfo
            {
                Name = name,
                Kind = kind,
                Start = nameStart,
                End = i,
                Owner = stack.Count > 0 ? stack[stack.Count - 1] : null,
                IsDeclaration = true,
            });
        }
    }

    private static void SkipLabelAfterColon(string text, int atWordEnd, int n, ref string? pendingLabel)
    {
        pendingLabel = null;
        int i = SkipWsComments(text, atWordEnd, n);
        if (i < n && text[i] == ':')
        {
            i = SkipWsComments(text, i + 1, n);
            while (i < n && IsIdentChar(text[i])) i++;
        }
    }

    // ---------------- small utils ----------------

    private static int SkipWsComments(string text, int i, int n)
    {
        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                continue;
            }
            break;
        }
        return i;
    }

    private static int SkipBracket(string text, int start, int n)
    {
        int depth = 0;
        int i = start;
        while (i < n)
        {
            char c = text[i];
            if (c == '[') depth++;
            else if (c == ']') { depth--; i++; if (depth == 0) return i; continue; }
            else if (c == '"')
            {
                i++;
                while (i < n && text[i] != '"') { if (text[i] == '\\') i++; i++; }
            }
            else if (c == '/' && i + 1 < n && text[i + 1] == '/') { while (i < n && text[i] != '\n') i++; continue; }
            else if (c == '/' && i + 1 < n && text[i + 1] == '*') { i += 2; while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/')) i++; i++; }
            i++;
        }
        return i;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$' || c == '\\';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsTypeWord(string w) =>
        w == "int" || w == "integer" || w == "bit" || w == "logic" || w == "reg" ||
        w == "byte" || w == "shortint" || w == "longint" || w == "time" ||
        w == "real" || w == "shortreal" || w == "realtime" || w == "string" ||
        w == "void" || w == "event" || w == "signed" || w == "unsigned";

    private static bool IsDeclarationTypeWord(string w) =>
        IsTypeWord(w) ||
        w == "wire" || w == "wand" || w == "wor" || w == "tri" || w == "tri0" || w == "tri1" ||
        w == "supply0" || w == "supply1" || w == "uwire" ||
        w == "parameter" || w == "localparam" || w == "specparam" ||
        w == "input" || w == "output" || w == "inout" || w == "ref" ||
        w == "var" || w == "const" || w == "genvar";

    private static SystemVerilogNamedElementKindLocal KindToNamedElementKind(ScopeKind kind) => kind switch
    {
        ScopeKind.Module => SystemVerilogNamedElementKindLocal.Module,
        ScopeKind.Interface => SystemVerilogNamedElementKindLocal.Interface,
        ScopeKind.Package => SystemVerilogNamedElementKindLocal.Package,
        ScopeKind.Program => SystemVerilogNamedElementKindLocal.Program,
        ScopeKind.Checker => SystemVerilogNamedElementKindLocal.Checker,
        ScopeKind.Primitive => SystemVerilogNamedElementKindLocal.Primitive,
        ScopeKind.Class => SystemVerilogNamedElementKindLocal.Class,
        _ => SystemVerilogNamedElementKindLocal.Unknown,
    };
}

public enum ScopeKind { Module, Interface, Package, Program, Checker, Primitive, Class }
public enum MemberKind { Function, Task }

public sealed class Scope
{
    public ScopeKind Kind;
    public string Name = "";
    public int NameStart;
    public int BodyStart;
    public string? Label;
    public Scope? Owner;
}

public sealed class ParseResult
{
    public List<SymbolInfo> Symbols = new();
    public List<ReferenceInfo> References = new();
    public List<DiagnosticInfo> Diagnostics = new();
    /// <summary>All opened building-block scopes in declaration order; Owner chain is set.</summary>
    public List<Scope> Scopes = new();
    /// <summary>Colouring tokens (keyword / comment / string / number / ...), in arbitrary order.</summary>
    public List<TokenInfo> Tokens = new();

    public void AddToken(TokenTypes type, int start, int end)
    {
        if (end > start) Tokens.Add(new TokenInfo { Type = type, Start = start, End = end });
    }
}

public enum TokenTypes
{
    Keyword = 0,
    Comment = 1,
    String = 2,
    Number = 3,
    Macro = 4,
    Function = 5,
    Type = 6,
    Variable = 7,
    Property = 8,   // net
    Parameter = 9,
    Register = 10,
    Identifier = 11,
}

public sealed class TokenInfo
{
    public TokenTypes Type;
    public int Start;
    public int End;
}

public sealed class SymbolInfo
{
    public string Name = "";
    public SystemVerilogNamedElementKindLocal Kind;
    public int Start;
    public int End;
    public Scope? Owner;
    public bool IsDeclaration;
}

public sealed class ReferenceInfo
{
    public string Name = "";
    public int Start;
    public int End;
}

public sealed class DiagnosticInfo
{
    public int Start;
    public int End;
    public string Message = "";
    public int Severity; // 1=error 2=warning 3=info 4=hint
}

public enum SystemVerilogNamedElementKindLocal
{
    Unknown, Module, Interface, Package, Program, Checker, Primitive, Class,
    Variable, Net, Parameter, LocalParameter, Port, Typedef, Function, Task,
}
