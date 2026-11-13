using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SystemVerilogLanguageServer
{
    /// <summary>
    /// Body of the <c>rtl/toolMenu</c> custom request: the tool menu (the
    /// desktop editor's Shift+Space list) as data, so the VS Code extension can
    /// render the same menu in a QuickPick.
    /// </summary>
    internal sealed class ToolMenuResultDto
    {
        /// <summary>Rows in menu order.</summary>
        [JsonPropertyName("entries")]
        public List<ToolMenuEntryDto> Entries { get; set; } = new();

        /// <summary>Why the menu is empty (only set when it is).</summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>One row of the tool menu.</summary>
    internal sealed class ToolMenuEntryDto
    {
        /// <summary>
        /// Stable tool id. The client maps it to a VS Code command, so it must
        /// match one of the ids in the plugin's <c>ToolMenuCatalog.ToolId</c>.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Menu label.</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>One line explaining what the tool does.</summary>
        [JsonPropertyName("detail")]
        public string Detail { get; set; } = "";

        /// <summary><c>false</c> when the tool cannot run at the caret.</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>Why the tool cannot run (only set when disabled).</summary>
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        /// <summary>
        /// The tool needs a window only the desktop editor owns, so this host
        /// can never run it.
        /// </summary>
        [JsonPropertyName("desktopOnly")]
        public bool DesktopOnly { get; set; }
    }
}
