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

                    PatchPlan plan = PatchPlanner.Create(clientRoot, manifest);

                    write("up to date " + plan.UpToDateCount
                        + ", missing " + plan.MissingCount
                        + ", wrong size " + plan.WrongSizeCount
                        + ", changed " + plan.WrongContentCount);

                    if (plan.IsUpToDate)
                    {
                        outcome.Summary = "Client is up to date (manifest " + manifest.Version + ").";
                        write(outcome.Summary);
                        return outcome;
                    }

                    int total = plan.DownloadCount;
                    int done = 0;
                    List<string> failures = new List<string>();

                    write("Updating " + total + " file(s), " + Megabytes(plan.BytesToDownload) + "...");

                    foreach (PlannedFile file in plan.Downloads)
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
