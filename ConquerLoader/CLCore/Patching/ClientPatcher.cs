using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;

namespace CLCore.Patching
{
    /// <summary>
    /// What the patch attempt decided, in the two terms the caller needs: may
    /// the game start, and what does the player get told.
    /// </summary>
    public sealed class PatchOutcome
    {
        /// <summary>Whether the loader should go on and start the client.</summary>
        public bool CanLaunch = true;

        /// <summary>
        /// Shown to the player when <see cref="CanLaunch"/> is false. Written to
        /// be read by somebody who wanted to play a game, not by a developer.
        /// </summary>
        public string Message;

        /// <summary>One line for conquerloader.log, always set.</summary>
        public string Summary;

        /// <summary>True when at least one file was actually written.</summary>
        public bool ChangedAnything;

        /// <summary>
        /// The loader replaced itself and the caller must start
        /// <see cref="RelaunchImagePath"/> and stop being this process. Issue #53.
        ///
        /// NOTHING ELSE WAS PATCHED in a run that sets this. The content check is
        /// left to the restarted process, so the files are compared by the loader
        /// version that shipped with the manifest describing them rather than by
        /// the one that is about to be replaced.
        /// </summary>
        public bool RelaunchRequired;

        /// <summary>Where the replacement loader now is. Set with <see cref="RelaunchRequired"/>.</summary>
        public string RelaunchImagePath;
    }

    /// <summary>
    /// Brings an installed client into line with the server's patch layer, then
    /// says whether it is safe to launch. Runs from the loader's launch path,
    /// before the client process is started.
    ///
    /// FAILURE HAS TO BE VISIBLE, AND THE DEFAULT DIFFERS BY FAILURE. The patch
    /// site being unreachable has nothing to do with whether the game server is
    /// up, so "I could not check for updates" must not become "you cannot play".
    /// But a file that could not be written IS a broken install, and a client
    /// missing art it expects fails in ways that look like a server bug. So:
    ///
    ///   * The manifest could not be fetched  -> LAUNCH. Nothing is known to be
    ///     wrong with the install; only the update check failed.
    ///   * Files could not be updated         -> DO NOT LAUNCH. Something IS
    ///     known to be wrong with the install.
    ///
    /// Everything it decides is written to the loader's log through
    /// <c>log</c>, because the alternative - a launcher that silently does or
    /// does not update - is the failure this whole feature exists to remove.
    /// </summary>
    public static class ClientPatcher
    {
        /// <summary>
        /// Long enough for a large file on a domestic line, short enough that a
        /// black-holed connection does not look like a hung launcher.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Checks <paramref name="clientRoot"/> against the patch layer at
        /// <paramref name="baseUrl"/> and repairs what differs.
        ///
        /// <paramref name="log"/> receives one line per decision.
        /// <paramref name="progress"/> receives (done, total) as files are
        /// written, so a caller with a progress bar can move it; both may be
        /// null.
        /// </summary>
        public static PatchOutcome Run(string clientRoot, Uri baseUrl, TimeSpan timeout, Action<string> log, Action<int, int> progress)
        {
            return Run(clientRoot, baseUrl, timeout, log, progress, null);
        }

        /// <summary>
        /// As above, and additionally keeps the loader itself in step.
        ///
        /// <paramref name="runningImagePath"/> is the executable this process is
        /// running from, or null to disable self-update entirely. Null is what
        /// every test and every caller that is not the launch path passes, and it
        /// is the behaviour this method had before issue #53.
        /// </summary>
        public static PatchOutcome Run(string clientRoot, Uri baseUrl, TimeSpan timeout, Action<string> log, Action<int, int> progress, string runningImagePath)
        {
            if (clientRoot == null) throw new ArgumentNullException(nameof(clientRoot));
            if (baseUrl == null) throw new ArgumentNullException(nameof(baseUrl));

            PatchOutcome outcome = new PatchOutcome();
            Action<string> write = log ?? delegate { };

            write("Patch check: " + clientRoot + " against " + baseUrl);

            if (!Directory.Exists(clientRoot))
            {
                // Not fatal to the launch: the loader is about to fail on its own
                // terms if the client really is not there, and it says so better.
                outcome.Summary = "Patch check skipped: no such client directory " + clientRoot + ".";
                write(outcome.Summary);
                return outcome;
            }

            // Before the fetch, so an image left by the previous launch is cleared
            // even on a run where the patch site turns out to be unreachable. It
            // could not have been deleted by the process that replaced it - that
            // process was still running from it.
            int swept = SelfUpdate.SweepSuperseded(runningImagePath, write);
            if (swept > 0)
                write("Removed " + swept + " superseded loader image(s) from the previous update.");

            // Set the moment the first byte is written anywhere. It is what
            // separates "we never touched the install" from "we touched it and
            // then something went wrong", and those two want opposite defaults.
            bool touchedTheInstall = false;

            try
            {
                using (PatchServer server = new PatchServer(baseUrl, timeout))
                {
                    Manifest manifest;

                    try
                    {
                        manifest = ManifestReader.Parse(server.GetManifestJson());
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is InvalidDataException)
                    {
                        // Default LAUNCH. This says nothing about whether the game
                        // server is up - it usually means the website is
                        // mid-deploy, or this machine is offline.
                        outcome.Summary = "Could not check for updates, launching anyway: " + ex.Message;
                        write(outcome.Summary);
                        return outcome;
                    }

                    write("Manifest " + manifest.Version + ", " + manifest.FileCount + " files. Hashing...");

                    // ---------------------------------------------------------
                    // The loader itself, before anything else. Issue #53.
                    // ---------------------------------------------------------
                    // FIRST, and alone in its run. A newer loader may read a
                    // manifest this one cannot, so the content comparison belongs
                    // to the version that shipped with the document describing it.
                    ManifestFile loaderEntry = FindRunningImage(manifest, clientRoot, runningImagePath);

                    if (loaderEntry != null && !MatchesOnDisk(loaderEntry, runningImagePath))
                    {
                        if (SelfUpdate.GuardIsSet())
                        {
                            // Already restarted once this launch and STILL out of
                            // date. Almost certainly the published bytes do not
                            // match the hash published for them - and the wrong
                            // answer here is to try again, which is a launcher
                            // that never launches on every machine at once. Say
                            // so, patch the content, and let the player play.
                            write("The loader is still out of date after restarting once. "
                                + "Leaving it alone - the patch layer is publishing a loader that does not match its own hash.");
                        }
                        else
                        {
                            write("Loader out of date (" + loaderEntry.Path + ", " + Megabytes(loaderEntry.Size) + "). Updating it first.");

                            string staged = null;

                            try
                            {
                                staged = server.DownloadStaged(loaderEntry, clientRoot);

                                string superseded = SelfUpdate.Replace(runningImagePath, staged);
                                staged = null;

                                outcome.ChangedAnything = true;
                                outcome.RelaunchRequired = true;
                                outcome.RelaunchImagePath = runningImagePath;
                                outcome.Summary = "Loader updated; restarting it before checking the client files.";

                                write("Replaced " + Path.GetFileName(runningImagePath)
                                    + "; the previous one is " + Path.GetFileName(superseded) + " and is removed on the next launch.");
                                write(outcome.Summary);

                                return outcome;
                            }
                            catch (LoaderNotRestoredException ex)
                            {
                                // The only failure here that costs the player
                                // anything: there is no loader at its own name any
                                // more. Said now, while there is still a running
                                // one to say it with, because the alternative is a
                                // shortcut that stops working tomorrow.
                                outcome.CanLaunch = false;
                                outcome.Summary = ex.Message;
                                outcome.Message =
                                    "The loader could not finish updating itself, and could not be put back."
                                    + Environment.NewLine + Environment.NewLine
                                    + "Your working loader is now called:" + Environment.NewLine
                                    + "    " + Path.GetFileName(ex.SupersededPath) + Environment.NewLine + Environment.NewLine
                                    + "Rename it back to " + Path.GetFileName(ex.ExpectedPath) + " and start it again."
                                    + Environment.NewLine
                                    + "Nothing else about your install was changed.";

                                write(outcome.Summary + Environment.NewLine + ex);
                                return outcome;
                            }
                            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is UnauthorizedAccessException)
                            {
                                // EVERY OTHER FAILURE LEAVES THE INSTALL AS IT WAS,
                                // so it must not stop the launch. A locked file
                                // during the rename - a scanner reading a 47 MB exe
                                // is the routine cause - would otherwise mean a
                                // player cannot play because an update they did not
                                // ask for could not be applied. The old loader is
                                // still the one running, and it still works.
                                write("Could not update the loader, carrying on with the one that is running: " + ex.Message);
                            }
                            finally
                            {
                                FileInstaller.Discard(staged);
                            }
                        }
                    }

                    PatchPlan plan = PatchPlanner.Create(clientRoot, manifest);

                    write("up to date " + plan.UpToDateCount
                        + ", missing " + plan.MissingCount
                        + ", wrong size " + plan.WrongSizeCount
                        + ", changed " + plan.WrongContentCount);

                    // BELT AND BRACES, and the one that stops a bricked launcher.
                    // Reaching FileInstaller with the running image means
                    // File.Delete on it, which is a sharing violation, which is
                    // counted as a file that could not be updated, which means DO
                    // NOT LAUNCH - forever, because the next run does exactly the
                    // same thing. The step above has already replaced the loader
                    // or deliberately declined to; either way this loop never
                    // touches it.
                    //
                    // Filtered out of the counts rather than skipped inside the
                    // loop, so the file count and the byte total describe what
                    // this loop will actually do.
                    List<PlannedFile> downloads = new List<PlannedFile>();
                    long bytesToDownload = 0;

                    foreach (PlannedFile file in plan.Downloads)
                    {
                        if (ReferenceEquals(file.Entry, loaderEntry)) continue;

                        downloads.Add(file);
                        bytesToDownload += file.Entry.Size;
                    }

                    // Otherwise the two lines read as a contradiction - "changed 1"
                    // followed by "up to date" - which is exactly the kind of log
                    // a support conversation gets stuck on.
                    if (loaderEntry != null && downloads.Count != plan.DownloadCount)
                        write("The loader is not counted above; it is updated by its own path and never by this loop.");

                    if (downloads.Count == 0)
                    {
                        outcome.Summary = "Client is up to date (manifest " + manifest.Version + ").";
                        write(outcome.Summary);
                        return outcome;
                    }

                    int total = downloads.Count;
                    int done = 0;
                    List<string> failures = new List<string>();

                    write("Updating " + total + " file(s), " + Megabytes(bytesToDownload) + "...");

                    foreach (PlannedFile file in downloads)
                    {
                        touchedTheInstall = true;

                        try
                        {
                            server.Download(file.Entry, clientRoot);
                            outcome.ChangedAnything = true;
                            write("  " + Describe(file.Reason) + " " + file.Entry.Path);
                        }
                        catch (Exception ex) when (ex is IOException || ex is HttpRequestException)
                        {
                            // Named and kept, then carried on. One unreachable
                            // file should not stop the rest from being repaired.
                            failures.Add(file.Entry.Path);
                            write("  FAILED " + file.Entry.Path + ": " + ex.Message);
                        }

                        done++;
                        if (progress != null) progress(done, total);
                    }

                    if (failures.Count > 0)
                    {
                        outcome.CanLaunch = false;
                        outcome.Summary = failures.Count + " of " + total + " file(s) could not be updated.";
                        outcome.Message =
                            failures.Count + " file(s) could not be updated, so the client is out of step with the server."
                            + Environment.NewLine + Environment.NewLine
                            + First(failures, 5)
                            + Environment.NewLine
                            + "Your install was left as it was for those files. Starting the loader again is"
                            + Environment.NewLine
                            + "safe and will retry only what is still wrong.";

                        write(outcome.Summary);
                        return outcome;
                    }

                    outcome.Summary = "Client updated to " + manifest.Version + " (" + total + " file(s)).";
                    write(outcome.Summary);
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                // An unexpected failure, which is a different question from the
                // handled ones above: we do not know what state the install is
                // in. If nothing had been written yet, nothing is known to be
                // wrong and the player should get to play. If files had already
                // been replaced, the install is half-patched and starting the
                // client would produce exactly the confusing, cosmetic, unreported
                // breakage this feature exists to prevent.
                outcome.CanLaunch = !touchedTheInstall;
                outcome.Summary = "Unexpected error while patching (" + ex.GetType().Name + "): " + ex.Message;
                write(outcome.Summary + Environment.NewLine + ex);

                if (!outcome.CanLaunch)
                {
                    outcome.Message =
                        "The update stopped part-way through and the client may be out of step with the server."
                        + Environment.NewLine + Environment.NewLine
                        + ex.Message
                        + Environment.NewLine + Environment.NewLine
                        + "Starting the loader again is safe and will retry only what is still wrong.";
                }

                return outcome;
            }
        }

        /// <summary>
        /// The manifest entry that names the executable this process is running
        /// from, or null if the layer carries no loader, if self-update is off,
        /// or if this process is running from outside the client root.
        ///
        /// The last of those is the developer case and it matters: a loader run
        /// from bin\Release against a client tree elsewhere must not rename
        /// itself into that tree. <see cref="SelfUpdate.IsRunningImage"/> gets it
        /// from resolving both sides to absolute paths rather than by any special
        /// case here.
        /// </summary>
        private static ManifestFile FindRunningImage(Manifest manifest, string clientRoot, string runningImagePath)
        {
            if (string.IsNullOrEmpty(runningImagePath)) return null;
            if (manifest == null || manifest.Files == null) return null;

            foreach (ManifestFile entry in manifest.Files)
                if (SelfUpdate.IsRunningImage(clientRoot, entry.Path, runningImagePath))
                    return entry;

            return null;
        }

        /// <summary>
        /// Whether the file at <paramref name="fullPath"/> is already what
        /// <paramref name="entry"/> describes. Size first, then hash, for the
        /// reason PatchPlanner gives - though at one file the saving is only ever
        /// the 47 MB read.
        /// </summary>
        private static bool MatchesOnDisk(ManifestFile entry, string fullPath)
        {
            if (!File.Exists(fullPath)) return false;
            if (new FileInfo(fullPath).Length != entry.Size) return false;

            return string.Equals(PatchPlanner.HashFile(fullPath), entry.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static string Describe(PatchReason reason)
        {
            switch (reason)
            {
                case PatchReason.Missing: return "restored";
                case PatchReason.WrongSize: return "repaired";
                case PatchReason.WrongContent: return "updated ";
                default: return "ok      ";
            }
        }

        private static string First(List<string> paths, int count)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < paths.Count && i < count; i++)
                lines.Add("    " + paths[i]);

            if (paths.Count > count)
                lines.Add("    ... and " + (paths.Count - count) + " more");

            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private static string Megabytes(long bytes)
        {
            if (bytes < 1024 * 1024)
                return Math.Max(1, bytes / 1024) + " KB";

            return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
        }
    }
}
