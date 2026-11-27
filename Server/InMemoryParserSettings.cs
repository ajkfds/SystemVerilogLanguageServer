using System;
using System.Collections.Generic;
using SystemVerilogCore.Documents;
using SystemVerilogCore.Projects;
using SystemVerilogCore.Verilog;
using SystemVerilogCore.Verilog.BuildingBlocks;
using SystemVerilogCore.Verilog.DataObjects.Variables;
using SystemVerilogCore.Verilog.Items;
using SystemVerilogCore.Verilog.Statements.SystemTask;
using AnnotationCommands = SystemVerilogCore.Verilog.AnnotationCommands;
using Macro = SystemVerilogCore.Verilog.Macro;
using RuleSet = SystemVerilogCore.Verilog.RuleSet;

namespace SystemVerilogLanguageServer.Server;

/// <summary>
/// The parser settings this host presents: the plugin's settings, with the one
/// host-specific hook replaced.
///
/// <para>
/// <b>Why a wrapper instead of the plugin object directly.</b> The language
/// server parses with its own in-memory files, but the settings it hands the
/// parser were the plugin's <c>ProjectProperty</c>. That is fine for every
/// member except <see cref="IVerilogParserSettings.CreateInstanceItem"/>: the
/// plugin's implementation builds an editor item (<c>VerilogModuleInstance</c>,
/// which wraps a <c>TextFile</c>) so the editor's item tree can show the
/// instance. This host has no item tree - the hierarchy is derived from the
/// parse results on demand - and the contract explicitly allows returning null
/// ("a host without an item tree returns null and the instance is not
/// recorded").
/// </para>
///
/// <para>
/// Returning null is not just permissible, it is what makes the parse work for
/// this host: with the plugin's implementation in place, an instance of a module
/// declared by this host could not be built (the wrap needs an editor file), so
/// the on-demand instance parse was skipped entirely - the include inside a
/// module body stopped resolving, the simulation setup missed the instantiated
/// module, and parameter overrides were not applied during the parent's
/// re-parse. Measured: six tests, all from this one hook.
/// </para>
///
/// <para>
/// Everything else forwards unchanged, so the two hosts still share one parser
/// behaviour: same name spaces (they are the cross-file symbol tables), same
/// macros, same rule sets, same system task / function tables.
/// </para>
/// </summary>
internal sealed class InMemoryParserSettings : IVerilogParserSettings, IVerilogProjectSettings
{
    private readonly IVerilogParserSettings inner;

    public InMemoryParserSettings(IVerilogParserSettings inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    // ---- IVerilogProjectSettings -------------------------------------
    // The UI-free settings view. Same values, under the names that contract
    // uses (it is the one a host without the parser can implement).

    public string ID => (inner as IVerilogProjectSettings)?.ID ?? string.Empty;

    IVerilogNameSpace IVerilogProjectSettings.DefinitionNameSpace =>
        (inner as IVerilogProjectSettings)!.DefinitionNameSpace;

    IVerilogNameSpace IVerilogProjectSettings.PackageNameSpace =>
        (inner as IVerilogProjectSettings)!.PackageNameSpace;

    IVerilogNameSpace IVerilogProjectSettings.UnitNameSpace =>
        (inner as IVerilogProjectSettings)!.UnitNameSpace;

    // ---- name spaces -------------------------------------------------

    public RegisterNameSpace<BuildingBlock> DefinitionNameSpace => inner.DefinitionNameSpace;

    public RegisterNameSpace<Package> PackageNameSpace => inner.PackageNameSpace;

    public RegisterNameSpace<INamedElement> UnitNameSpace => inner.UnitNameSpace;

    // ---- parser tables -----------------------------------------------

    public IReadOnlyDictionary<string, Macro> Macros => inner.Macros;

    public IReadOnlyDictionary<string, Func<WordScanner, NameSpace, SystemTask>?> SystemTaskParsers =>
        inner.SystemTaskParsers;

    public IReadOnlyDictionary<string, Func<Variable, WordScanner>?> SystemFunctions =>
        inner.SystemFunctions;

    // ---- annotations and rules ---------------------------------------

    public AnnotationCommands AnnotationCommands => inner.AnnotationCommands;

    public string AnnotationKeyValueDelimiter => inner.AnnotationKeyValueDelimiter;

    public RuleSet RuleSet => inner.RuleSet;

    public RuleSet GetRuleSet(string? relativePath) => inner.GetRuleSet(relativePath);

    // ---- external libraries ------------------------------------------

    public IReadOnlyDictionary<string, string> ExtenralModuleLibraryPath =>
        inner.ExtenralModuleLibraryPath;

    public IReadOnlyDictionary<string, string> ExtenralPrimitiveLibraryPath =>
        inner.ExtenralPrimitiveLibraryPath;

    // ---- instance resolution -----------------------------------------

    public BuildingBlock? GetInstancedBuildingBlock(IBuildingBlockInstantiation instantiation) =>
        inner.GetInstancedBuildingBlock(instantiation);

    // ---- instance item creation --------------------------------------

    /// <summary>
    /// Null: this host has no item tree to record the instance in.
    ///
    /// <para>
    /// The contract allows this, and the parser then drives the instance parse
    /// itself (<c>ParseEngine.ParseInstanceAsync</c> records the result under the
    /// parameter-qualified key, which is what the readers look up). The editor
    /// keeps its implementation so its NavigatePanel shows the node.
    /// </para>
    /// </summary>
    public IVerilogInstanceSource? CreateInstanceItem(IBuildingBlockInstantiation instantiation) =>
        null;

    // ---- completion candidates that expand on insert ------------------

    public IVerilogAutocompleteItem? CreateModuleInstanceCandidate(string moduleName) =>
        inner.CreateModuleInstanceCandidate(moduleName);

    public IVerilogAutocompleteItem? CreateKeywordCandidate(string keyword) =>
        inner.CreateKeywordCandidate(keyword);

    // ---- completion candidate enumeration -----------------------------

    public void AppendKeywordCandidates(
        List<IVerilogAutocompleteItem> items,
        string candidate,
        int candidateStartIndex,
        int lineStartIndex,
        bool systemVerilog,
        Func<IVerilogAutocompleteItem, bool> itemFilter) =>
        inner.AppendKeywordCandidates(
            items, candidate, candidateStartIndex, lineStartIndex, systemVerilog, itemFilter);

    public bool GetAutoCompleteTarget(
        IVerilogSourceFile item,
        ParsedDocument parsedDocument,
        int index,
        out NameSpace? nameSpace,
        out INamedElement? element,
        out string candidate,
        out int candidateStartIndex) =>
        inner.GetAutoCompleteTarget(
            item, parsedDocument, index,
            out nameSpace, out element, out candidate, out candidateStartIndex);
}
