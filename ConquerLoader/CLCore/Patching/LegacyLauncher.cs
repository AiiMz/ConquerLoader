using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace CLCore.Patching
{
    /// <summary>
    /// The launcher was renamed from ConquerLoader.exe to EternalAbyss.exe, and
    /// this is how an install that predates the rename gets there.
    ///
    /// WHY A HAND-OFF RATHER THAN A RENAME ON DISK. The patch layer decides what
    /// a player has by path, not by identity: publish the loader under a new name
    /// and every existing install simply downloads a second 45 MB file and goes
    /// on launching the old one, which then never self-updates again - it becomes
    /// the one piece of code that cannot be fixed, which is the exact condition
    /// issue #53 existed to remove. Renaming the file under a running process is
    /// possible (see <see cref="SelfUpdate"/>) but it strands the shortcut the
    /// player already has, and a shortcut to a missing exe is a support ticket
    /// that reads "the game is gone".
    ///
    /// So both names ship, and they are THE SAME BUILD. The exe decides what it
    /// is from the name it was started under: run as EternalAbyss.exe it is the
    /// launcher, and run as ConquerLoader.exe it starts its own sibling and
    /// leaves. An old shortcut keeps working forever, the player ends up in the
    /// new process either way, and the legacy copy stays current because the
    /// patcher treats it as ordinary content whenever it is not the running
    /// image.
    ///
    /// WHY AT STARTUP RATHER THAN AFTER PATCHING. The patch pass runs on the
    /// launch path, with a window up and the player waiting for the game, and
    /// swapping launchers there would replace the window they just clicked Play
    /// in. At startup there is nothing to disturb. The cost is that the very
    /// first run after the rename ships has no sibling to hand off to yet - the
    /// patch pass installs it later in that same session - so that one run is
    /// served by the old name and every run after it hands off.
    ///
    /// NOTHING HERE MAY STOP THE LOADER FROM STARTING. Every failure - no
    /// sibling, an unreadable directory, a refused Process.Start - means "carry
    /// on as this process", because a launcher that will not open is worse than
    /// one wearing the wrong name.
    /// </summary>
    public static class LegacyLauncher
    {
        /// <summary>The name the loader shipped under until the rename.</summary>
        public const string LegacyName = "ConquerLoader.exe";

        /// <summary>The name it ships under now, and the one it hands off to.</summary>
        public const string CurrentName = "EternalAbyss.exe";

        /// <summary>
        /// Set on the process this hands off to, so a hand-off can never become a
        /// chain.
        ///
        /// It should be impossible for the new process to hand off again - it is
        /// running from EternalAbyss.exe, and that is not the legacy name - but
        /// the check costs nothing and the failure it prevents is a fork bomb of
        /// launcher windows on a player's machine. A separate variable from
        /// <see cref="SelfUpdate.GuardVariable"/> on purpose: a hand-off must not
        /// consume the self-update's one permitted hop, because the process being
        /// started is entitled to update itself.
        /// </summary>
        public const string GuardVariable = "ETERNALABYSS_HANDED_OFF";

        /// <summary>True on a process that was started by a hand-off.</summary>
        public static bool GuardIsSet()
        {
            try
            {
                return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(GuardVariable));
            }
            catch (Exception)
            {
                // A denied environment block is not a reason to risk the chain.
                return true;
            }
        }

        /// <summary>
        /// The sibling this process should hand off to, or null to carry on.
        ///
        /// BY THE NAME THE PROCESS WAS STARTED UNDER, which is the one thing that
        /// distinguishes the two copies - they are the same bytes. A developer
        /// running from bin\Release is running ConquerLoader.exe too, but has no
        /// EternalAbyss.exe beside it, so the second condition is what keeps this
        /// out of their way.
        ///
        /// The sibling is required to be a non-empty file that is not this image.
        /// An install written by <see cref="FileInstaller"/> is atomic - it lands
        /// under a part name and is moved into place - so existence is proof of
        /// completeness, and the length check is there for a file that arrived
        /// some other way.
        /// </summary>
        public static string ResolveHandOff(string runningImagePath)
        {
            if (string.IsNullOrEmpty(runningImagePath)) return null;

            string image;
            string directory;

            try
            {
                image = Path.GetFullPath(runningImagePath);
                directory = Path.GetDirectoryName(image);
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            catch (IOException) { return null; }

            if (string.IsNullOrEmpty(directory)) return null;

            if (!string.Equals(Path.GetFileName(image), LegacyName, StringComparison.OrdinalIgnoreCase))
                return null;

            string sibling = Path.Combine(directory, CurrentName);

            // A case-insensitive file system cannot hold both names at once only
            // if they are spelled the same, which they are not - but a junction,
            // a hard link or a copy of this exe under the new name would all
            // resolve here, and handing off to ourselves is the one outcome that
            // never terminates.
            if (string.Equals(sibling, image, StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                FileInfo info = new FileInfo(sibling);
                if (!info.Exists || info.Length <= 0) return null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            return sibling;
        }

        /// <summary>
        /// Starts <paramref name="imagePath"/> with this process's arguments.
        ///
        /// The working directory is the loader's own folder rather than this
        /// process's, for the same reason the patch step uses it: the client is
        /// often started from an Env_DX8 or Env_DX9 subfolder, and the new
        /// process has to see the client root the way a double-click would.
        /// </summary>
        public static void HandOff(string imagePath, IEnumerable<string> arguments, Action<string> log)
        {
            if (string.IsNullOrEmpty(imagePath)) throw new ArgumentNullException(nameof(imagePath));

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = imagePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(imagePath)),
                Arguments = SelfUpdate.QuoteArguments(arguments),
            };

            start.EnvironmentVariables[GuardVariable] = "1";

            if (log != null)
                log("Started under the old name; handing off to " + Path.GetFileName(imagePath) + ".");

            Process.Start(start);
        }
    }
}
