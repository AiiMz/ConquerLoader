using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace CLCore.Patching
{
    /// <summary>
    /// Parses and validates a manifest.
    ///
    /// Every rejection here throws <see cref="InvalidDataException"/> with a
    /// sentence naming what was wrong, because the reader of that sentence is a
    /// player pasting it into a support conversation, not a developer with a
    /// debugger. "Unhandled exception: System.NullReferenceException" is the
    /// failure this class exists to not produce.
    /// </summary>
    public static class ManifestReader
    {
        public static Manifest Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("The manifest was empty.");

            Manifest manifest;
            try
            {
                // MissingMemberHandling stays at its default (Ignore): a manifest
                // carrying a field this loader has not been taught yet is a
                // FORWARD-compatible document, and schemaVersion below is what
                // decides whether that is allowed. Erroring here would refuse
                // documents the version guard was written to accept.
                manifest = JsonConvert.DeserializeObject<Manifest>(json);
            }
            catch (JsonException ex)
            {
                // The usual cause is a captive portal or a proxy answering with
                // an HTML login page, which is a 200 full of angle brackets.
                throw new InvalidDataException("The manifest is not valid JSON (" + ex.Message + ").");
            }

            if (manifest == null)
                throw new InvalidDataException("The manifest parsed to nothing.");

            Validate(manifest);
            return manifest;
        }

        public static void Validate(Manifest manifest)
        {
            if (manifest.SchemaVersion != Manifest.SupportedSchemaVersion)
            {
                throw new InvalidDataException(
                    "This loader understands manifest schemaVersion " + Manifest.SupportedSchemaVersion +
                    ", and the server is serving " + manifest.SchemaVersion +
                    ". Download the current client from the website - this one is too old to update itself.");
            }

            if (manifest.Files == null)
                throw new InvalidDataException("The manifest has no files list.");

            // Both counts are redundant, and that is the point: a manifest
            // truncated in transit still parses, and would otherwise read as a
            // shorter list of files rather than as damage.
            if (manifest.Files.Count != manifest.FileCount)
            {
                throw new InvalidDataException(
                    "The manifest says it lists " + manifest.FileCount + " files but carries " +
                    manifest.Files.Count + ". It is incomplete.");
            }

            long total = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (ManifestFile file in manifest.Files)
            {
                if (file == null)
                    throw new InvalidDataException("The manifest contains an empty entry.");

                string problem;
                if (!ClientPaths.IsSafeRelativePath(file.Path, out problem))
                    throw new InvalidDataException("The manifest entry '" + file.Path + "' is not a path this loader will write: " + problem + ".");

                // Ordinal, NOT case-insensitive. Two entries differing only in
                // case are two different files to the server, and collapsing
                // them here would silently drop one - which on Windows is
                // exactly the pair that then fights over one local file.
                if (!seen.Add(file.Path))
                    throw new InvalidDataException("The manifest lists '" + file.Path + "' twice.");

                if (file.Size < 0)
                    throw new InvalidDataException("The manifest entry '" + file.Path + "' has a negative size.");

                if (!IsSha256Hex(file.Sha256))
                    throw new InvalidDataException("The manifest entry '" + file.Path + "' does not carry a SHA-256 hash.");

                total += file.Size;
            }

            if (total != manifest.TotalBytes)
            {
                throw new InvalidDataException(
                    "The manifest says it totals " + manifest.TotalBytes + " bytes but its entries add up to " +
                    total + ". It is inconsistent with itself.");
            }
        }

        /// <summary>
        /// 64 hex characters. Checked because a hash that is not one can only
        /// come from a document that is not ours, and comparing against it would
        /// mean re-downloading every file on every run forever - a slow, silent,
        /// bandwidth-shaped failure rather than a message.
        /// </summary>
        public static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != 64)
                return false;

            foreach (char c in value)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }
    }
}
