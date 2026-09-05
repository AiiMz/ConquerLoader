using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CLCore.Patching
{
    public enum PatchReason
    {
        UpToDate,
        Missing,
        WrongSize,
        WrongContent,
    }

    public sealed class PlannedFile
    {
        public ManifestFile Entry;
        public PatchReason Reason;

        public bool NeedsDownload
        {
            get { return Reason != PatchReason.UpToDate; }
        }
    }

    public sealed class PatchPlan
    {
        public List<PlannedFile> Files = new List<PlannedFile>();

        public int UpToDateCount;
        public int MissingCount;
        public int WrongSizeCount;
        public int WrongContentCount;

        public long BytesToDownload;

        public bool IsUpToDate
        {
            get { return MissingCount == 0 && WrongSizeCount == 0 && WrongContentCount == 0; }
        }

        public int DownloadCount
        {
            get { return MissingCount + WrongSizeCount + WrongContentCount; }
        }

        public IEnumerable<PlannedFile> Downloads
        {
            get
            {
                foreach (PlannedFile f in Files)
                    if (f.NeedsDownload)
                        yield return f;
            }
        }
    }

    /// <summary>
    /// Decides what has to be downloaded.
    ///
    /// HASHES, NOT VERSIONS. A version stamp records what SHOULD be true. A file
    /// half-written by a crashed download, or hand-edited by a curious player, is
    /// exactly the case where the stamp is right and the file is wrong - and it
    /// is a case that will happen. Hashing the layer costs a second or two and
    /// turns "repair my install" from a support conversation into the normal code
    /// path.
    ///
    /// SIZE FIRST, THEN HASH. Size is a stat rather than a read, so the common
    /// damage - a truncated download - is caught without hashing a 7.6 MB file,
    /// and the full hash still runs on everything that survives that test.
    ///
    /// WRONG CONTENT AND MISSING ARE THE SAME ACTION AND DIFFERENT NEWS. Both
    /// download the file; only one of them means something on this machine
    /// changed a file the server owns. They are counted separately so the
    /// summary can say which happened, because "3 files repaired" and "3 files
    /// updated" send a player to very different conclusions.
    /// </summary>
    public static class PatchPlanner
    {
        public static PatchPlan Create(string clientRoot, Manifest manifest)
        {
            if (clientRoot == null) throw new ArgumentNullException(nameof(clientRoot));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            PatchPlan plan = new PatchPlan();

            foreach (ManifestFile entry in manifest.Files)
            {
                // Throws on anything unsafe. Validate() has already run over the
                // whole document, so reaching this is a bug rather than input.
                string full = ClientPaths.Resolve(clientRoot, entry.Path);

                PatchReason reason;

                if (!File.Exists(full))
                {
                    reason = PatchReason.Missing;
                }
                else if (new FileInfo(full).Length != entry.Size)
                {
                    reason = PatchReason.WrongSize;
                }
                else if (!string.Equals(HashFile(full), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    reason = PatchReason.WrongContent;
                }
                else
                {
                    reason = PatchReason.UpToDate;
                }

                plan.Files.Add(new PlannedFile { Entry = entry, Reason = reason });

                switch (reason)
                {
                    case PatchReason.UpToDate: plan.UpToDateCount++; break;
                    case PatchReason.Missing: plan.MissingCount++; break;
                    case PatchReason.WrongSize: plan.WrongSizeCount++; break;
                    case PatchReason.WrongContent: plan.WrongContentCount++; break;
                }

                if (reason != PatchReason.UpToDate)
                    plan.BytesToDownload += entry.Size;
            }

            return plan;
        }

        /// <summary>
        /// NOTHING HERE DELETES ANYTHING, and the omission is deliberate. The
        /// client root holds tens of thousands of files this manifest says
        /// nothing about - Conquer.exe, the .wdf archives, the player's own
        /// screenshots and their config.json. A patcher that removed what it did
        /// not recognise would eat all of it.
        ///
        /// The cost is that a file dropped from the patch layer stays on disk
        /// forever. That is the right trade at this size: a stale file is inert,
        /// and the alternative failure deletes a player's install.
        /// </summary>
        public static string HashFile(string fullPath)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(fullPath))
                return ToHex(sha.ComputeHash(stream));
        }

        /// <summary>
        /// Lowercase hex. Hand-rolled because Convert.ToHexString is .NET 5+ and
        /// this assembly targets .NET Framework; BitConverter.ToString would
        /// produce "AB-CD-EF" and need two more string allocations to undo.
        /// </summary>
        public static string ToHex(byte[] bytes)
        {
            StringBuilder text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                text.Append(b.ToString("x2"));

            return text.ToString();
        }
    }
}
