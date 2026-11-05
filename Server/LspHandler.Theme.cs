using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// <c>rtl/theme</c> — the semantic-token colour palette, derived from the
/// plugin's <c>CodeDrawStyle</c> so the editor and the VS Code extension share
/// one source of truth.
///
/// <para>
/// This is a non-standard request (vendor prefix <c>rtl/</c>): the standard
/// <c>semanticTokens</c> result carries token types, never colours, so the
/// palette has to travel on its own channel. The client writes the returned
/// rules into <c>editor.semanticTokenColorCustomizations</c>.
/// </para>
///
/// <para>
/// The colours are the plugin's own RGB values, adjusted for a dark VS Code
/// theme where the original (tuned for the editor's light background) would
/// read as white. The mapping from <c>ColorType</c> to LSP token type lives
/// here, next to the palette, so adding a token type only touches this file.
/// </para>
/// </summary>
public sealed partial class LspHandler
{
    /// <summary>
    /// LSP semantic token type → plugin <c>CodeDrawStyle.ColorType</c>. The
    /// order of the token types must match the legend advertised in
    /// <c>initialize</c> (see <see cref="SemanticTokenTypes"/>).
    /// </summary>
    private static readonly (string TokenType, pluginVerilog.CodeDrawStyle.ColorType ColorType)[] TokenColorMap =
    {
        ("keyword", pluginVerilog.CodeDrawStyle.ColorType.Keyword),
        ("comment", pluginVerilog.CodeDrawStyle.ColorType.Comment),
        ("string", pluginVerilog.CodeDrawStyle.ColorType.Number),
        ("number", pluginVerilog.CodeDrawStyle.ColorType.Number),
        ("macro", pluginVerilog.CodeDrawStyle.ColorType.Parameter),
        ("function", pluginVerilog.CodeDrawStyle.ColorType.Identifier),
        ("type", pluginVerilog.CodeDrawStyle.ColorType.Identifier),
        ("variable", pluginVerilog.CodeDrawStyle.ColorType.Variable),
        ("property", pluginVerilog.CodeDrawStyle.ColorType.Net),
        ("parameter", pluginVerilog.CodeDrawStyle.ColorType.Parameter),
        ("register", pluginVerilog.CodeDrawStyle.ColorType.Register),
        ("identifier", pluginVerilog.CodeDrawStyle.ColorType.Identifier),
    };

    /// <summary>
    /// The semantic token types, in legend order. Shared by the capability
    /// advertisement and the theme so the two can never drift apart.
    /// </summary>
    internal static readonly string[] SemanticTokenTypes =
    {
        "keyword", "comment", "string", "number", "macro",
        "function", "type", "variable", "property", "parameter", "register", "identifier",
    };

    /// <summary>
    /// Returns the colour rules for <c>editor.semanticTokenColorCustomizations</c>.
    /// </summary>
    private object? HandleTheme(JsonElement parameters, CancellationToken cancellationToken)
    {
        pluginVerilog.CodeDrawStyle style = new();
        Dictionary<string, string> rules = new();

        foreach ((string tokenType, pluginVerilog.CodeDrawStyle.ColorType colorType) in TokenColorMap)
        {
            Avalonia.Media.Color color = style.GetColor(colorType);
            rules[tokenType] = ToHex(color);
        }

        return new ThemeResult
        {
            Rules = rules,
            // the legend the client must use for the semantic tokens provider
            TokenTypes = new List<string>(SemanticTokenTypes),
        };
    }

    /// <summary>Formats a colour as <c>#RRGGBB</c> (VS Code settings format).</summary>
    private static string ToHex(Avalonia.Media.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
