using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CLCore.Patching
{
    /// <summary>
    /// The self-update went wrong AND could not be undone, so the loader is no
    /// longer at its own name.
    ///
    /// It exists to be distinguishable, because it is the only failure in this
    /// feature that costs a player something. Every other way a self-update can
    /// fail leaves the install exactly as it was and is worth no more than a line
    /// in the log; this one means the next launch finds no ConquerLoader.exe, so
    /// it has to be said out loud while there is still a running loader to say it
    /// with.
    /// </summary>
    public sealed class LoaderNotRestoredException : Exception
    {
        /// <summary>Where the working loader actually is now.</summary>
        public readonly string SupersededPath;

        /// <summary>The name it should be moved back to.</summary>
        public readonly string ExpectedPath;

        public LoaderNotRestoredException(string expectedPath, string supersededPath, Exception inner)
            : base("The loader could not be replaced and could not be put back. It is now "
                   + Path.GetFileName(supersededPath) + " and needs renaming to "
                   + Path.GetFileName(expectedPath) + ".", inner)
        {
            ExpectedPath = expectedPath;
            SupersededPath = supersededPath;
        }
    }

    /// <summary>
    /// Replacing the loader with the copy the patch layer carries. Issue #53.
    ///
    /// WHY THIS EXISTS AT ALL. Every file the patcher handles is inert data
    /// except one: the loader itself. It shipped only inside the 1.4 GB base
    /// archive, so it was the newest code in the project and the only piece that
    /// could never be fixed after a player had it - nobody re-downloads 1.4 GB
    /// without a reason. The content files change weekly and cannot break a
    /// launch; the loader is the thing most likely to need a fix and was the
    /// least able to receive one.
    ///
    /// WHY IT NEEDS ITS OWN CODE PATH. Windows will not let a process delete or
    /// overwrite its own running image, and <see cref="FileInstaller"/> does
    /// exactly that - delete, then move. Handing it the running exe produces a
    /// sharing violation, which the patcher counts as a file it could not update,
    /// which means DO NOT LAUNCH. So the naive version of "just put the loader in
    /// the manifest" is not a no-op that works by luck: it is a client that
    /// refuses to start, on every launch, permanently.
    ///
    /// WHAT WINDOWS DOES ALLOW is renaming a running image. So the running exe is
    /// moved aside, the verified replacement is moved into its name, and the
    /// process restarts from the new one. The old image stays on disk until the
    /// next launch sweeps it, because it is still mapped by the process doing the
    /// sweeping.
    ///
    /// THE ORDER MATTERS AND IS NOT AN OPTIMISATION. The loader is updated first
    /// and nothing else is touched in that run. A new loader may understand a
    /// manifest an old one does not, and the content patch should be performed by
    /// the version that shipped alongside the manifest describing it.
    /// </summary>
    public static class SelfUpdate
    {
        /// <summary>
        /// What the superseded image is renamed to.
        ///
        /// ".old" DELIBERATELY, because two existing rules already know that
        /// suffix: EternalAbyssCo's ManifestBuilder.IsBackup keeps it out of the
        /// patch layer, and New-ClientArchive.ps1 cuts it from the shipped
        /// archive by the same test. A novel suffix would need both of them
        /// taught about it, and would be forgotten in one of the two.
        /// </summary>
        public const string SupersededSuffix = ".old";

        /// <summary>
        /// Set on the restarted process, and the whole reason a bad publish
        /// cannot become an infinite restart loop.
        ///
        /// The loop is real rather than theoretical: if the manifest ever names a
        /// hash the published exe does not actually have, then every run would
        /// find the loader out of date, replace it, restart, and find it out of
        /// date again - a launcher that never launches, on every player machine
        /// at once, fixable only by a new download. One hop is all this is ever
        /// allowed.
        /// </summary>
        public const string GuardVariable = "CONQUERLOADER_SELFUPDATED";

        /// <summary>
        /// The file this process is running from.
        ///
        /// MainModule first because the loader is a Costura-packed single file
        /// and this is the question Windows itself answers. The assembly location
        /// is the fallback for the case where MainModule is not readable, and
        /// null - meaning "do not self-update" - is preferred over a guess: a
        /// wrong answer here renames the wrong file.
        /// </summary>
        public static string RunningImagePath()
        {
            try
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    string path = self.MainModule == null ? null : self.MainModule.FileName;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch (Exception)
            {
                // Denied, or a platform that will not say. Fall through.
            }

            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                string location = entry == null ? null : entry.Location;
                return string.IsNullOrEmpty(location) ? null : location;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True on a process this feature has already restarted once.</summary>
        public static bool GuardIsSet()
        {
            try
            {
                return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GuardVariable));
            }
            catch (Exception)
            {
                // A denied environment block is not a reason to risk the loop.
                return true;
            }
        }

        /// <summary>
        /// Whether <paramref name="manifestPath"/> names the file this process is
        /// running from.
        ///
        /// BY RESOLVED PATH, NEVER BY NAME. Comparing "is this entry called
        /// ConquerLoader.exe" would self-update a developer running from
        /// bin\Release against some other tree manifest, and would miss a player
        /// whose loader sits in a client root the entry reaches by a different
        /// spelling. Resolving both sides and comparing absolute paths also gives
        /// the containment check for free: an image outside the client root can
        /// never match an entry inside it.
        /// </summary>
        public static bool IsRunningImage(string clientRoot, string manifestPath, string runningImagePath)
        {
            if (string.IsNullOrEmpty(runningImagePath)) return false;
            if (string.IsNullOrEmpty(manifestPath)) return false;

            string resolved;
            string running;

            try
            {
                resolved = ClientPaths.Resolve(clientRoot, manifestPath);
                running = Path.GetFullPath(runningImagePath);
            }
            catch (InvalidDataException)
            {
                // An unsafe manifest path is not the running image, and saying so
                // here keeps this a predicate. The normal path refuses it loudly.
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            return string.Equals(resolved, running, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The rename dance. Moves the running image aside and moves
        /// <paramref name="stagedPath"/> into its place, returning where the old
        /// image went.
        ///
        /// IF THE SECOND MOVE FAILS THE FIRST IS UNDONE. That window - the loader
        /// renamed away and its replacement not yet in place - is the only moment
        /// this feature can leave a player with no launcher at all, so it is the
        /// one failure that gets an explicit restore rather than an exception
        /// travelling up. The restore is attempted best-effort and never replaces
        /// the original exception, which is the one that says what went wrong.
        /// </summary>
        public static string Replace(string runningImagePath, string stagedPath)
        {
            if (string.IsNullOrEmpty(runningImagePath)) throw new ArgumentNullException(nameof(runningImagePath));
            if (string.IsNullOrEmpty(stagedPath)) throw new ArgumentNullException(nameof(stagedPath));

            if (!File.Exists(stagedPath))
                throw new FileNotFoundException("The staged loader is not where it was written.", stagedPath);

            string superseded = ChooseSupersededName(runningImagePath);

            // Permitted on a running image, unlike delete and unlike overwrite.
            File.Move(runningImagePath, superseded);

            try
            {
                File.Move(stagedPath, runningImagePath);
            }
            catch (Exception replaceFailed)
            {
                try
                {
                    File.Move(superseded, runningImagePath);
                }
                catch (Exception)
                {
                    // Both moves failed, so the loader is sitting under the
                    // superseded name and nothing is at its own. That is the one
                    // outcome here a player pays for, and it gets its own type so
                    // the caller can say so rather than logging a line nobody
                    // reads until the next launch finds no launcher.
                    throw new LoaderNotRestoredException(runningImagePath, superseded, replaceFailed);
                }

                throw;
            }

            return superseded;
        }

        /// <summary>
        /// Picks a free name for the superseded image, reusing the plain ".old"
        /// whenever the previous one can be cleared.
        ///
        /// The numbered fallbacks keep the suffix LAST rather than appending a
        /// digit after it, so every one of them still ends in ".old" and stays
        /// covered by the two backup rules this suffix was chosen for.
        /// </summary>
        private static string ChooseSupersededName(string runningImagePath)
        {
            string plain = runningImagePath + SupersededSuffix;

            if (!File.Exists(plain))
                return plain;

            try
            {
                File.Delete(plain);
                if (!File.Exists(plain)) return plain;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            for (int n = 2; n < 100; n++)
            {
                string candidate = runningImagePath + "." + n + SupersededSuffix;
                if (!File.Exists(candidate)) return candidate;
            }

            throw new IOException(
                "Could not find a free name to move the running loader aside; " +
                Path.GetFileName(runningImagePath) + SupersededSuffix + " and 98 numbered variants all exist.");
        }

        /// <summary>
        /// Deletes images left by previous self-updates.
        ///
        /// AT THE START OF A RUN, not at the end of one: at the end the old image
        /// is still mapped by this very process and cannot be deleted. The next
        /// launch is the first moment it is free, which is why this leaves a file
        /// on disk between the two and why failing to remove one is not an error.
        ///
        /// Scoped to this image own name. A sweep of "*.old" beside the loader
        /// would be a program that deletes player files because they happened to
        /// name one the way it names its own.
        /// </summary>
        public static int SweepSuperseded(string runningImagePath, Action<string> log)
        {
            if (string.IsNullOrEmpty(runningImagePath)) return 0;

            string directory;
            string name;

            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(runningImagePath));
                name = Path.GetFileName(runningImagePath);
            }
            catch (Exception)
            {
                return 0;
            }

            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name)) return 0;
            if (!Directory.Exists(directory)) return 0;

            List<string> candidates = new List<string>();

            try
            {
                candidates.AddRange(Directory.EnumerateFiles(directory, name + "*" + SupersededSuffix));
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }

            int removed = 0;

            foreach (string candidate in candidates)
            {
                // EnumerateFiles matches on the 8.3 short name as well as the long
                // one, so the pattern alone is not proof of the shape.
                if (!IsSupersededImage(name, Path.GetFileName(candidate))) continue;

                try
                {
                    File.Delete(candidate);
                    removed++;
                }
                catch (IOException)
                {
                    // Still mapped, or locked by a scanner reading it. It is inert
                    // either way and the next launch tries again.
                    if (log != null) log("Could not remove the superseded loader " + Path.GetFileName(candidate) + " yet; it will be retried next launch.");
                }
                catch (UnauthorizedAccessException)
                {
                    if (log != null) log("Not permitted to remove the superseded loader " + Path.GetFileName(candidate) + ".");
                }
            }

            return removed;
        }

        /// <summary>
        /// True for exactly the names <see cref="ChooseSupersededName"/> produces
        /// from <paramref name="imageName"/>: "&lt;image&gt;.old" and
        /// "&lt;image&gt;.&lt;n&gt;.old".
        /// </summary>
        public static bool IsSupersededImage(string imageName, string candidateName)
        {
            if (string.IsNullOrEmpty(imageName) || string.IsNullOrEmpty(candidateName)) return false;

            if (!candidateName.EndsWith(SupersededSuffix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!candidateName.StartsWith(imageName, StringComparison.OrdinalIgnoreCase)) return false;

            string middle = candidateName.Substring(
                imageName.Length,
                candidateName.Length - imageName.Length - SupersededSuffix.Length);

            if (middle.Length == 0) return true;
            if (middle[0] != '.') return false;

            for (int i = 1; i < middle.Length; i++)
                if (middle[i] < '0' || middle[i] > '9')
                    return false;

            return middle.Length > 1;
        }

        /// <summary>
        /// Starts the replacement loader and hands it the arguments this process
        /// was given, with <see cref="GuardVariable"/> set so it cannot update
        /// itself in turn.
        ///
        /// UseShellExecute false because that is what makes the environment
        /// variable reachable - and it is the variable, not the restart, that
        /// bounds this to one hop.
        /// </summary>
        public static void Relaunch(string imagePath, IEnumerable<string> arguments, Action<string> log)
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = imagePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(imagePath)),
                Arguments = QuoteArguments(arguments),
            };

            start.EnvironmentVariables[GuardVariable] = "1";

            if (log != null) log("Restarting " + Path.GetFileName(imagePath) + " to finish the update.");

            Process.Start(start);
        }

        /// <summary>
        /// Re-quotes an argument list into a command line the restarted process
        /// will split back into the same arguments.
        ///
        /// THE RULE IS NOT "ESCAPE EVERY BACKSLASH", which is the version written
        /// first and the version that survives reading. CommandLineToArgvW, which
        /// is what the new process parses with, treats a backslash as an escape
        /// ONLY when a run of them is immediately followed by a quote; everywhere
        /// else it is a literal. So doubling them all turns a perfectly ordinary
        /// Windows path into one with doubled separators - which is what a
        /// rehearsal against a real restarted process showed, and what no amount
        /// of rereading the method showed.
        ///
        /// Hence: a run of N backslashes becomes 2N before a quote or at the end
        /// of a quoted argument, and stays N anywhere else.
        /// </summary>
        public static string QuoteArguments(IEnumerable<string> arguments)
        {
            if (arguments == null) return string.Empty;

            List<string> parts = new List<string>();

            foreach (string argument in arguments)
            {
                if (argument == null) continue;
                parts.Add(Quote(argument));
            }

            return string.Join(" ", parts.ToArray());
        }

        private static string Quote(string argument)
        {
            if (argument.Length > 0
                && argument.IndexOf(' ') < 0
                && argument.IndexOf('\t') < 0
                && argument.IndexOf('"') < 0)
            {
                return argument;
            }

            System.Text.StringBuilder quoted = new System.Text.StringBuilder();
            quoted.Append('"');

            int backslashes = 0;

            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    // The run escapes itself, then escapes the quote.
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(c);
            }

            // A trailing run sits immediately before the closing quote, so it is
            // in the escaping position even though nothing in the argument is.
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');

            return quoted.ToString();
        }
    }
}
