using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SystemVerilogLanguageServer
{
    /// <summary>
    /// Body of the <c>rtl/snippet</c> custom request: the text the editor's
    /// snippet would insert, so the VS Code extension can apply it with
    /// <c>TextEditor.insertSnippet</c> instead of re-implementing the logic.
    /// </summary>
    internal sealed class SnippetResultDto
    {
        /// <summary>Snippets to apply (usually one).</summary>
        [JsonPropertyName("snippets")]
        public List<SnippetDto> Snippets { get; set; } = new();

        /// <summary>
        /// Candidate names when the request needs a further choice (e.g. which
        /// module to instantiate). The client shows a picker and re-requests
        /// with the chosen name.
        /// </summary>
        [JsonPropertyName("candidates")]
        public List<string> Candidates { get; set; } = new();

        /// <summary>Why nothing was produced (shown to the user).</summary>
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    /// <summary>One snippet the client can insert or apply as a replacement.</summary>
    internal sealed class SnippetDto
    {
        /// <summary>Human readable name (command palette / picker label).</summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>Short description of what the snippet does.</summary>
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        /// <summary><c>insert</c> (at the caret) or <c>replace</c> (over a range).</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "insert";

        /// <summary>Replacement range start (only meaningful for <c>replace</c>).</summary>
        [JsonPropertyName("start")]
        public int Start { get; set; }

        /// <summary>Replacement range end, exclusive (only for <c>replace</c>).</summary>
        [JsonPropertyName("end")]
        public int End { get; set; }

        /// <summary>VS Code snippet text (<c>${1:name}</c> tab stops).</summary>
        [JsonPropertyName("snippetText")]
        public string SnippetText { get; set; } = "";
    }
}
