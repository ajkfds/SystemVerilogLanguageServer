using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemVerilogLanguageServer
{
    /// <summary>
    /// Opaque token that identifies a node to expand in the RTL Hierarchy tree.
    ///
    /// <para>
    /// The client never interprets it: it receives <c>expandToken</c> on a node
    /// and echoes it back verbatim in the next <c>rtl/hierarchy</c> request.
    /// That replaces the old <c>nodePath</c> index sequence, which the client
    /// had to build itself (<c>parentPath.concat([index])</c>) and which broke
    /// whenever the tree changed between two requests (an index then pointed at
    /// a different node).
    /// </para>
    ///
    /// <para>
    /// The payload is a small JSON object, base64url-encoded so it is a single
    /// opaque string on the wire. It is not a security boundary (the server
    /// validates every field it reads); encoding only keeps the client from
    /// depending on the shape.
    /// </para>
    /// </summary>
    internal static class ExpandToken
    {
        /// <summary>What the token addresses.</summary>
        internal enum TokenKind
        {
            /// <summary>The workspace root (top level of the tree).</summary>
            Workspace,
            /// <summary>A workspace folder (expand its directory listing).</summary>
            Folder,
            /// <summary>A file (expand its first instance level).</summary>
            File,
            /// <summary>An instance inside a file (expand its children).</summary>
            Instance,
        }

        /// <summary>Decoded payload of an expand token.</summary>
        internal sealed class Payload
        {
            public TokenKind Kind { get; init; }

            /// <summary>Workspace-relative folder path (Folder tokens).</summary>
            public string? FolderPath { get; init; }

            /// <summary>File URI the node lives in (File / Instance tokens).</summary>
            public string? Uri { get; init; }

            /// <summary>
            /// Stable path of the instance inside the file: the chain of child
            /// names from the file root ("u_mid/u_leaf"). Names are used instead
            /// of indexes so the token stays valid when a sibling is added or
            /// removed between two requests.
            /// </summary>
            public IReadOnlyList<string>? InstancePath { get; init; }
        }

        /// <summary>Wire form of the payload (short names keep the token small).</summary>
        private sealed class WirePayload
        {
            [JsonPropertyName("k")] public string Kind { get; set; } = "w";
            [JsonPropertyName("f")] public string? FolderPath { get; set; }
            [JsonPropertyName("u")] public string? Uri { get; set; }
            [JsonPropertyName("p")] public List<string>? InstancePath { get; set; }
        }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Token for the workspace root (top level).</summary>
        public static string ForWorkspace() => Encode(new Payload { Kind = TokenKind.Workspace });

        /// <summary>Token for a workspace folder.</summary>
        public static string ForFolder(string folderPath) =>
            Encode(new Payload { Kind = TokenKind.Folder, FolderPath = folderPath });

        /// <summary>Token for a file (first instance level).</summary>
        public static string ForFile(string uri) =>
            Encode(new Payload { Kind = TokenKind.File, Uri = uri });

        /// <summary>Token for an instance inside a file.</summary>
        public static string ForInstance(string uri, IReadOnlyList<string> instancePath) =>
            Encode(new Payload { Kind = TokenKind.Instance, Uri = uri, InstancePath = instancePath });

        /// <summary>
        /// Decodes a token. Returns null when it is missing, malformed or
        /// carries an unknown kind, so the caller can answer with an empty list
        /// instead of throwing.
        /// </summary>
        public static Payload? Decode(string? token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            string json;
            try
            {
                json = Encoding.UTF8.GetString(Base64UrlDecode(token));
            }
            catch (FormatException)
            {
                return null;
            }

            WirePayload? wire;
            try
            {
                wire = JsonSerializer.Deserialize<WirePayload>(json, SerializerOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            if (wire == null) return null;

            TokenKind kind = wire.Kind switch
            {
                "w" => TokenKind.Workspace,
                "f" => TokenKind.Folder,
                "d" => TokenKind.File,
                "i" => TokenKind.Instance,
                _ => (TokenKind)(-1),
            };
            if ((int)kind < 0) return null;

            return new Payload
            {
                Kind = kind,
                FolderPath = wire.FolderPath,
                Uri = wire.Uri,
                InstancePath = wire.InstancePath,
            };
        }

        private static string Encode(Payload payload)
        {
            WirePayload wire = new()
            {
                Kind = payload.Kind switch
                {
                    TokenKind.Workspace => "w",
                    TokenKind.Folder => "f",
                    TokenKind.File => "d",
                    _ => "i",
                },
                FolderPath = payload.FolderPath,
                Uri = payload.Uri,
                InstancePath = payload.InstancePath is null ? null : new List<string>(payload.InstancePath),
            };
            string json = JsonSerializer.Serialize(wire, SerializerOptions);
            return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string text)
        {
            string padded = text.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }
    }
}
