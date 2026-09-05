using System.Collections.Generic;
using Newtonsoft.Json;

namespace CLCore.Patching
{
    /// <summary>
    /// The document the patch server serves at &lt;baseUrl&gt;/manifest.json.
    ///
    /// The JSON is the contract between this loader and whatever generates the
    /// patch layer, and <see cref="SupportedSchemaVersion"/> is the guard on it.
    /// </summary>
    public class Manifest
    {
        /// <summary>
        /// The only version this loader understands. A document declaring
        /// anything else is refused rather than guessed at: an older loader
        /// reading a newer document would silently skip fields it does not know,
        /// and "silently skips things" is the one behaviour a file-sync tool must
        /// never have.
        /// </summary>
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Diagnostic only. Logged so a player can read it back in a support
        /// conversation and so the server and the client can be compared, never
        /// used to decide whether to download anything - see PatchPlanner.
        /// </summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        /// <summary>
        /// Both are redundant against <see cref="Files"/>, and both are checked
        /// against it. A truncated or partially-written manifest is otherwise a
        /// perfectly valid document describing a client that is missing files -
        /// which would read as "nothing to do" rather than as an error.
        /// </summary>
        [JsonProperty("fileCount")]
        public int FileCount { get; set; }

        [JsonProperty("totalBytes")]
        public long TotalBytes { get; set; }

        [JsonProperty("files")]
        public List<ManifestFile> Files { get; set; }
    }

    public class ManifestFile
    {
        /// <summary>
        /// Relative to the client root, forward slashes, case exactly as the
        /// generator emitted it. A Conquer tree contains ItemMinIcon.Ani and
        /// Common.Ani next to ChatEmotion.ani; a case-sensitive web server
        /// serves those as three different names and the wrong case is a 404.
        ///
        /// It arrives over the network, so it is validated before it is ever
        /// combined with a local directory. See <see cref="ClientPaths"/>.
        /// </summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        /// <summary>Lowercase hex SHA-256 of the file's bytes.</summary>
        [JsonProperty("sha256")]
        public string Sha256 { get; set; }
    }
}
