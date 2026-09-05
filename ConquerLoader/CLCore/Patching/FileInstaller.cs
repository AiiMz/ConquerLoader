using System;
using System.IO;
using System.Security.Cryptography;

namespace CLCore.Patching
{
    /// <summary>
    /// Writes one downloaded file into the client tree.
    ///
    /// TEMP FILE, HASH WHILE WRITING, THEN MOVE. Three properties fall out of
    /// that order and all three matter on a player's machine:
    ///
    ///   * A download interrupted halfway - closed lid, dropped wifi, killed
    ///     process - leaves a .part file and an untouched original, rather than
    ///     a half-written file where the client expects art.
    ///   * The hash is computed from the bytes that were actually written, not
    ///     from the bytes that were meant to be written. Hashing the file
    ///     afterwards would read it back through the same cache that just
    ///     accepted it.
    ///   * A file that fails its hash never reaches its destination at all, so a
    ///     failed patch leaves the install exactly as it was.
    ///
    /// The temp file is a sibling of the destination rather than in %TEMP%,
    /// because a move across volumes is a copy, and the atomicity is the whole
    /// point.
    /// </summary>
    public static class FileInstaller
    {
        public const string PartialSuffix = ".eapatch-part";

        /// <summary>
        /// Streams <paramref name="content"/> into place, verifying it against
        /// <paramref name="entry"/>. Throws <see cref="InvalidDataException"/> if
        /// the bytes do not match what the manifest promised, leaving the
        /// existing file untouched.
        /// </summary>
        public static void Install(string clientRoot, ManifestFile entry, Stream content)
        {
            string destination = ClientPaths.Resolve(clientRoot, entry.Path);
            string directory = Path.GetDirectoryName(destination);

            // New content arrives in directories that may not exist yet - a new
            // garment's mesh folder is the routine case.
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string partial = destination + PartialSuffix;
            string actualHash;
            long written;

            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                using (CryptoStream hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
                {
                    content.CopyTo(hashing);
                    hashing.FlushFinalBlock();
                    written = file.Length;
                    actualHash = PatchPlanner.ToHex(sha.Hash);
                }

                if (written != entry.Size)
                {
                    throw new InvalidDataException(
                        entry.Path + ": expected " + entry.Size + " bytes, received " + written + ".");
                }

                if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        entry.Path + ": the downloaded bytes do not match the hash the manifest gave for them.");
                }

                // File.Move has no overwrite overload on .NET Framework, so the
                // destination is removed first. That is a window in which the
                // file does not exist - acceptable because the client is not
                // running yet (this is a PRE-launch step) and because the
                // alternative, copying over the original in place, is the
                // half-written file this whole class exists to avoid.
                if (File.Exists(destination))
                    File.Delete(destination);

                File.Move(partial, destination);
            }
            finally
            {
                // Both the failure path and a successful move leave nothing
                // behind. A .part file that survives a killed process is not
                // swept by anything: it is inert, and the next attempt at the
                // same file opens it FileMode.Create and reuses it. Walking
                // 71,000 files to hunt for orphans would cost more than they do.
                if (File.Exists(partial))
                {
                    try { File.Delete(partial); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
