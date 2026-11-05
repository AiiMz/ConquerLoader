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

            // Whatever a previous update moved aside for this path is normally free
            // by now; clear it before possibly needing the name again.
            SweepMovedAside(destination);

            string partial = Stage(clientRoot, entry, content);

            try
            {
                // File.Move has no overwrite overload on .NET Framework, so the
                // destination is removed first. That is a window in which the
                // file does not exist - acceptable because the client is not
                // running yet (this is a PRE-launch step) and because the
                // alternative, copying over the original in place, is the
                // half-written file this whole class exists to avoid.
                string movedAside = null;

                if (File.Exists(destination))
                {
                    try
                    {
                        File.Delete(destination);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // IN USE IS NOT BROKEN. A DLL or exe mapped by a running
                        // process cannot be deleted ("access denied") but CAN be
                        // renamed - the same property SelfUpdate relies on. The
                        // routine causes are a second client already running
                        // (Conquer.exe, the injected GarmentMotionHook.dll) and a
                        // plugin the loader itself has loaded. The process holding
                        // the old bytes keeps them; the next start gets the new
                        // file, and the moved-aside one is swept later.
                        movedAside = ChooseMovedAsideName(destination);
                        File.Move(destination, movedAside);
                    }
                }

                try
                {
                    File.Move(partial, destination);
                }
                catch (Exception)
                {
                    // Put the original back rather than leave nothing at its name.
                    if (movedAside != null)
                    {
                        try { File.Move(movedAside, destination); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }

                    throw;
                }
            }
            finally
            {
                Discard(partial);
            }
        }

        /// <summary>
        /// Suffix for a file moved aside because it was in use. Ends in ".old" so
        /// ManifestBuilder.IsBackup and New-ClientArchive.ps1 already exclude it,
        /// and carries ".eapatch" so a sweep can never take a player's own
        /// "Conquer.exe.old" backup for one of ours.
        /// </summary>
        public const string MovedAsideSuffix = ".eapatch.old";

        /// <summary>A free name for moving <paramref name="destination"/> aside.</summary>
        internal static string ChooseMovedAsideName(string destination)
        {
            string plain = destination + MovedAsideSuffix;
            if (!File.Exists(plain)) return plain;

            for (int n = 2; n < 100; n++)
            {
                string candidate = destination + "." + n + MovedAsideSuffix;
                if (!File.Exists(candidate)) return candidate;
            }

            throw new IOException(
                "Could not find a free name to move " + Path.GetFileName(destination) + " aside; it is in use and "
                + Path.GetFileName(plain) + " and 98 numbered variants all exist.");
        }

        /// <summary>
        /// Best-effort removal of files earlier updates moved aside for
        /// <paramref name="destination"/>. One still in use stays for next time.
        /// </summary>
        public static void SweepMovedAside(string destination)
        {
            if (string.IsNullOrEmpty(destination)) return;

            // Numbered names are only ever created while the plain one exists, so
            // the common case - nothing moved aside - costs one existence check
            // per file rather than ninety-nine.
            string plain = destination + MovedAsideSuffix;
            if (!File.Exists(plain)) return;

            Discard(plain);
            for (int n = 2; n < 100; n++)
                Discard(destination + "." + n + MovedAsideSuffix);
        }

        /// <summary>
        /// The first half of <see cref="Install"/>: writes and verifies the
        /// sibling .part file and returns its path WITHOUT moving it into place.
        /// The caller owns the file from then on and must move or discard it.
        ///
        /// It is split out for exactly one caller - <see cref="SelfUpdate"/>,
        /// which cannot use the second half. A running executable can be renamed
        /// but not deleted or overwritten, so the loader replacing itself needs
        /// the verified bytes on disk and its own way of putting them in place.
        /// Sharing the first half means the loader is verified by the same code
        /// as every other file, which is the half that matters: the download is
        /// hashed while it is written, and bytes that do not match the manifest
        /// never become a file anybody can run.
        /// </summary>
        public static string Stage(string clientRoot, ManifestFile entry, Stream content)
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

                return partial;
            }
            catch (Exception)
            {
                // A staged file that failed verification never becomes anybody's
                // problem. On success the caller owns it instead, which is why
                // this is a catch rather than a finally.
                Discard(partial);
                throw;
            }
        }

        /// <summary>
        /// Removes a staged file, best-effort.
        ///
        /// A .part file that survives a killed process is not swept by anything:
        /// it is inert, and the next attempt at the same file opens it
        /// FileMode.Create and reuses it. Walking 71,000 files to hunt for
        /// orphans would cost more than they do.
        /// </summary>
        public static void Discard(string partialPath)
        {
            if (string.IsNullOrEmpty(partialPath)) return;
            if (!File.Exists(partialPath)) return;

            try { File.Delete(partialPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
