namespace CLCore.Models
{
    public class ServerConfiguration
    {
        public string ServerName { get; set; }
        public uint ServerVersion { get; set; }
        public string LoginHost { get; set; }
        public string GameHost { get; set; }
        public uint LoginPort { get; set; }
        public uint GamePort { get; set; }
        public string ExecutableName { get; set; }
        public bool EnableHostName { get; set; }
        public bool UseDirectX9 { get; set; }
        public string Hostname { get; set; }
        public string ServerNameMemoryAddress { get; set; }
        public string ServerIcon { get; set; }

        /// <summary>
        /// Where this server publishes its patch layer, e.g.
        /// "https://example.com/client/". The loader expects manifest.json
        /// directly under it and each file under files/&lt;path&gt;.
        ///
        /// Null or empty - which is what every existing config.json has - turns
        /// patching off entirely and the loader behaves exactly as it always did.
        /// Per server rather than global because the whole point of this loader
        /// is that one install can point at several of them, and a client is only
        /// ever in step with one at a time.
        /// </summary>
        public string PatchManifestUrl { get; set; }

        public ServerDatGroup Group { get; set; }

        override public string ToString()
        {
            return $"{ServerName} [{ServerVersion}]";
        }
    }
}
