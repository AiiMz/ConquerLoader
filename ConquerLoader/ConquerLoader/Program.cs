using System;
using System.Windows;
using CLCore.Patching;
using ConquerLoader.Forms.WPF;

namespace ConquerLoader
{
    static class Program
    {
        /// <summary>
        /// Punto de entrada principal para la aplicacion.
        /// </summary>
        [STAThread]
        static void Main()
        {
            if (HandedOffToNewName()) return;

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new MainLite());
        }

        /// <summary>
        /// The launcher was renamed to EternalAbyss.exe; this is the old name
        /// stepping aside. See <see cref="LegacyLauncher"/> for why both names
        /// ship and why this happens before any window exists.
        ///
        /// BEFORE EnableVisualStyles AND BEFORE THE Application OBJECT, so the
        /// hand-off costs a player nothing visible: no splash, no window that
        /// appears and closes. Returning true means this process has done its
        /// whole job.
        ///
        /// A FAILURE HERE IS NOT AN ERROR. If the sibling cannot be started -
        /// antivirus, a locked file, a denied environment block - this process
        /// carries on and is the launcher, which is exactly what it was before
        /// the rename. That is why the catch is broad and silent beyond the log.
        ///
        /// DO NOT GO LOOKING FOR THE SUCCESS LINE IN conquerloader.log. LogWritter
        /// keeps the session in memory and rewrites the whole file on every call,
        /// so the process this starts truncates the line saying it was started -
        /// by writing its own first line, which is the better evidence anyway.
        /// The failure line survives, because on that path nothing else runs.
        /// </summary>
        private static bool HandedOffToNewName()
        {
            try
            {
                if (LegacyLauncher.GuardIsSet()) return false;

                string target = LegacyLauncher.ResolveHandOff(SelfUpdate.RunningImagePath());
                if (target == null) return false;

                string[] all = Environment.GetCommandLineArgs();
                string[] arguments = new string[Math.Max(0, all.Length - 1)];
                if (arguments.Length > 0) Array.Copy(all, 1, arguments, 0, arguments.Length);

                LegacyLauncher.HandOff(target, arguments, line => Core.LogWritter.Write("[Launcher] " + line));
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    Core.LogWritter.Write("[Launcher] Could not hand off to " + LegacyLauncher.CurrentName
                        + " (" + ex.Message + "). Continuing as " + LegacyLauncher.LegacyName + ".");
                }
                catch (Exception)
                {
                    // The log is not a reason to fail to start.
                }

                return false;
            }
        }
    }
}
