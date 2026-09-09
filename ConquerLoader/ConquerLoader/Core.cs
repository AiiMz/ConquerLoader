using CLCore;
using CLCore.Models;
using ConquerLoader.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ConquerLoader
{
    public static class Core
    {
        public static LogWritter LogWritter = new LogWritter("conquerloader.log");
        public static string ConfigJsonPath = "config.json";
        public static bool UseEncryptedConfig = false;
        public static List<TextTranslation> TextTranslations = new List<TextTranslation>();
        public static LoaderConfig GetLoaderConfig()
        {
            LoaderConfig lConfig = null;
            if (File.Exists(ConfigJsonPath + ".lock"))
            {
                UseEncryptedConfig = true;
                lConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<LoaderConfig>(ConfigFilesEncryption.AESEncription.DecryptString(Constants.LockConfigurationKey, File.ReadAllText(ConfigJsonPath + ".lock")));
            }
            else if (File.Exists(ConfigJsonPath))
            {
                UseEncryptedConfig = false;
                lConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<LoaderConfig>(File.ReadAllText(ConfigJsonPath));
            }
            if (lConfig != null)
            {
                DetectLang(lConfig);
            }
            return lConfig;
        }
        public static void DetectLang(LoaderConfig lConfig)
        {
            if (lConfig.Lang != null)
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo(lConfig.Lang);
                Thread.CurrentThread.CurrentUICulture = new CultureInfo(lConfig.Lang);
            }
            else
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("en");
                Thread.CurrentThread.CurrentUICulture = new CultureInfo("en");
            }
            switch (Thread.CurrentThread.CurrentCulture.Name)
            {
                case "es":
                    {
                        TextTranslations = Newtonsoft.Json.JsonConvert.DeserializeObject<List<TextTranslation>>(Encoding.UTF8.GetString(Properties.Resources.lang_es));
                        break;
                    }
                case "en":
                    {
                        TextTranslations = Newtonsoft.Json.JsonConvert.DeserializeObject<List<TextTranslation>>(Encoding.UTF8.GetString(Properties.Resources.lang_en));
                        break;
                    }
                case "pt":
                    {
                        TextTranslations = Newtonsoft.Json.JsonConvert.DeserializeObject<List<TextTranslation>>(Encoding.UTF8.GetString(Properties.Resources.lang_pt));
                        break;
                    }
            }
        }
        public static string TranslateText(string key, string fallback = null)
        {
            TextTranslation translation = TextTranslations.Where(x => x.Id == key).FirstOrDefault();
            if (translation != null && !string.IsNullOrWhiteSpace(translation.Text))
            {
                return translation.Text;
            }

            return fallback ?? key;
        }
        public static int DirectXVersion()
        {
            int directxMajorVersion = 0;
            var OSVersion = Environment.OSVersion;
            // if Windows Vista or later
            if (OSVersion.Version.Major >= 6)
            {
                // if Windows 7 or later
                if (OSVersion.Version.Major > 6 || OSVersion.Version.Minor >= 1)
                {
                    directxMajorVersion = 11;
                }
                // if Windows Vista
                else
                {
                    directxMajorVersion = 10;
                }
            }
            // if Windows XP or earlier.
            else
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX"))
                {
                    string versionStr = key.GetValue("Version") as string;
                    if (!string.IsNullOrEmpty(versionStr))
                    {
                        var versionComponents = versionStr.Split('.');
                        if (versionComponents.Length > 1)
                        {
                            int directXLevel;
                            if (int.TryParse(versionComponents[1], out directXLevel))
                            {
                                directxMajorVersion = directXLevel;
                            }
                        }
                    }
                }
            }

            return directxMajorVersion;
        }

        internal static void LoadAvailablePlugins()
        {
            try
            {
                PluginLoader loader = new PluginLoader();
                loader.LoadPlugins();
                LogWritter.Write("Loaded " + PluginLoader.Plugins.Count + " enabled plugins.");
            }
            catch (Exception e)
            {
                LogWritter.Write(string.Format("Plugins couldn't be loaded: {0}", e.Message));
                Environment.Exit(0);
            }
        }

        public static void LoadRemotePlugins()
        {
            try
            {
                PluginLoader loader = new PluginLoader();
                int available = loader.LoadPluginsFromAPI(GetLoaderConfig()).Result;
                Core.LogWritter.Write("Remote plugin catalog reports " + available + " available plugins.");
            }
            catch (Exception ex)
            {
                Core.LogWritter.Write("Error remote plugins init: " + ex.ToString());
            }
        }

        public static void InitPlugins()
        {

            foreach (IPlugin plugin in PluginLoader.Plugins)
            {
                try
                {
                    plugin.Init();
                    LogWritter.Write("Init plugin: " + plugin.Name + ".");
                }
                catch (Exception ex)
                {
                    {
                        MessageBox.Show($"Error loading plugin {plugin.Name}: " + ex.Message.ToString(), "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }

        /// <summary>
        /// Brings the client into line with the selected server's patch layer
        /// before it is launched. Does nothing unless that server carries a
        /// <see cref="ServerConfiguration.PatchManifestUrl"/>, so a config.json
        /// written for any earlier version of this loader behaves exactly as it
        /// did.
        ///
        /// THIS IS WHY THE PATCHER IS NOT A SEPARATE EXECUTABLE. A patcher that
        /// ships beside the loader is a patcher nothing makes anyone run: the
        /// loader is the name players already know, it is what the first desktop
        /// shortcut points at, and a player who never runs the patcher is a
        /// player whose client silently disagrees with the server about item and
        /// mesh data. The symptoms of that are cosmetic and strange - missing
        /// garments, wrong icons - rather than an error anybody reports usefully.
        /// Putting the check inside the launch path is the only version of this
        /// that cannot be skipped by accident.
        ///
        /// It returns a <see cref="PluginPreLaunchResult"/> rather than a bool so
        /// the two call sites can reuse the cancel-launch plumbing they already
        /// have for plugins.
        ///
        /// The patch target is the loader's own folder, not the working
        /// directory: manifest paths are relative to the client ROOT, and the
        /// working directory is an Env_DX8 or Env_DX9 subfolder whenever one of
        /// those is in use.
        /// </summary>
        public static PluginPreLaunchResult RunAutoPatch(PluginPreLaunchContext context)
        {
            string manifestUrl = context?.Server?.PatchManifestUrl;

            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                return PluginPreLaunchResult.Success();
            }

            Uri baseUrl;
            if (!Uri.TryCreate(EnsureTrailingSlash(manifestUrl.Trim()), UriKind.Absolute, out baseUrl))
            {
                // Configuration is wrong rather than the install, so this is the
                // one patch failure that is worth stopping for even though
                // nothing has been touched: silently not patching is precisely
                // the behaviour being removed.
                string message = "PatchManifestUrl for \"" + context.Server.ServerName + "\" is not a valid URL: " + manifestUrl;
                LogWritter.Write(message);
                return PluginPreLaunchResult.Fail(message);
            }

            Action<int, int> reportProgress = null;
            if (context.ReportProgress != null)
            {
                // The launch path gives plugins a 1-8 band to move a progress bar
                // through. Downloads land in the same band so the window is not
                // simply frozen while a few hundred megabytes arrive.
                reportProgress = (done, total) =>
                {
                    if (total <= 0) return;
                    context.ReportProgress(1 + (int)(7L * done / total));
                };
            }

            CLCore.Patching.PatchOutcome outcome = CLCore.Patching.ClientPatcher.Run(
                context.StartupPath,
                baseUrl,
                CLCore.Patching.ClientPatcher.DefaultTimeout,
                line => LogWritter.Write("[Patch] " + line),
                reportProgress,
                CLCore.Patching.SelfUpdate.RunningImagePath());

            if (outcome.RelaunchRequired)
            {
                // The loader on disk is no longer the one running. Issue #53.
                //
                // Environment.Exit rather than a graceful close: this runs on the
                // launch path with a window up and a BackgroundWorker mid-flight,
                // and every orderly way out of here goes on to start the game with
                // the process that has just been superseded. There is nothing to
                // save - the patch step keeps no state, and config.json was
                // written when the player changed it.
                RestartAfterSelfUpdate(outcome.RelaunchImagePath);
            }

            if (!outcome.CanLaunch)
            {
                return PluginPreLaunchResult.Fail(outcome.Message ?? outcome.Summary);
            }

            return PluginPreLaunchResult.Success();
        }

        /// <summary>
        /// Starts the replacement loader and stops being this one.
        ///
        /// IF THE RESTART CANNOT BE STARTED, CARRY ON. The new loader is in place
        /// and verified either way, so the worst case is that this launch is
        /// served by the old process and the next one picks up the new file. That
        /// is strictly better than refusing to start the game because a Process
        /// .Start failed.
        /// </summary>
        private static void RestartAfterSelfUpdate(string imagePath)
        {
            try
            {
                string[] all = Environment.GetCommandLineArgs();
                string[] arguments = new string[Math.Max(0, all.Length - 1)];
                if (arguments.Length > 0) Array.Copy(all, 1, arguments, 0, arguments.Length);

                CLCore.Patching.SelfUpdate.Relaunch(imagePath, arguments, line => LogWritter.Write("[Patch] " + line));
            }
            catch (Exception ex)
            {
                LogWritter.Write("[Patch] The updated loader is in place but could not be started (" + ex.Message
                    + "). Continuing with this one; the update takes effect next launch.");
                return;
            }

            Environment.Exit(0);
        }

        /// <summary>
        /// Applies the player wing preference to the client before it starts.
        ///
        /// AFTER THE PATCH STEP, ALWAYS. The patcher compares by hash, so it
        /// restores Action3DEffect.ini the moment it sees the marker; running
        /// this first would simply have the edit undone a second later. Running
        /// it second means the stock file arrives and the preference is
        /// re-applied on top, every launch, with no state kept anywhere.
        ///
        /// IT NEVER CANCELS A LAUNCH, which is why it returns nothing. The
        /// server is not told about this setting and does not read the file, so
        /// the worst a failure can do is draw wings the player asked to hide, or
        /// hide wings they asked for. Neither is worth refusing to start the
        /// game over, and both are written to the log.
        /// </summary>
        public static void ApplyWingVisibility(PluginPreLaunchContext context)
        {
            bool hide = context != null && context.LoaderConfig != null && context.LoaderConfig.HideWings;

            CLCore.ClientOptions.WingVisibility.Apply(
                context == null ? null : context.StartupPath,
                hide,
                line => LogWritter.Write("[Wings] " + line));
        }

        private static string EnsureTrailingSlash(string url)
        {
            return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
        }

        public static PluginPreLaunchResult RunPreLaunchPlugins(PluginPreLaunchContext context)
        {
            if (PluginLoader.Plugins == null || PluginLoader.Plugins.Count == 0)
            {
                return PluginPreLaunchResult.Success();
            }

            foreach (IPlugin plugin in PluginLoader.Plugins)
            {
                IPreLaunchPlugin preLaunchPlugin = plugin as IPreLaunchPlugin;
                if (preLaunchPlugin == null)
                {
                    continue;
                }

                try
                {
                    LogWritter.Write("Running pre-launch plugin: " + plugin.Name + ".");
                    PluginPreLaunchResult result = preLaunchPlugin.BeforeLaunch(context) ?? PluginPreLaunchResult.Success();
                    if (!result.ContinueLaunch)
                    {
                        LogWritter.Write("Pre-launch plugin canceled launch: " + plugin.Name + ". Message: " + (result.Message ?? "No message."));
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    LogWritter.Write("Error on pre-launch plugin " + plugin.Name + ": " + ex);
                    return PluginPreLaunchResult.Fail("Plugin \"" + plugin.Name + "\" failed before launch: " + ex.Message);
                }
            }

            return PluginPreLaunchResult.Success();
        }

        public static void SaveLoaderConfig(LoaderConfig LoaderConfig)
        {
            if (UseEncryptedConfig)
            {
                File.WriteAllText(ConfigJsonPath + ".lock", ConfigFilesEncryption.AESEncription.EncryptString(Constants.LockConfigurationKey, Newtonsoft.Json.JsonConvert.SerializeObject(LoaderConfig, Newtonsoft.Json.Formatting.Indented)));
            } else
            {
                File.WriteAllText(ConfigJsonPath, Newtonsoft.Json.JsonConvert.SerializeObject(LoaderConfig, Newtonsoft.Json.Formatting.Indented));
            }
        }
        public static bool ServerAvailable(string Host, uint Port)
        {
            var result = false;
            using (var client = new TcpClient())
            {
                try
                {
                    client.ReceiveTimeout = 1 * 1000;
                    client.SendTimeout = 1 * 1000;
                    var asyncResult = client.BeginConnect(Host, (int)Port, null, null);
                    var waitHandle = asyncResult.AsyncWaitHandle;
                    try
                    {
                        if (!asyncResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(0.6), false))
                        {
                            // wait handle didn't came back in time
                            client.Close();
                        }
                        else
                        {
                            // The result was positiv
                            result = client.Connected;
                            // ensure the ending-call
                            client.EndConnect(asyncResult);
                        }
                    }
                    catch(Exception)
                    {
                    }
                    finally
                    {
                        // Ensure to close the wait handle.
                        waitHandle.Close();
                    }
                }
                catch(Exception)
                {
                }
            }
            return result;
        }

        public static void LoadControlTranslations(Control.ControlCollection Controls)
        {
            foreach (Control c in Controls)
            {
                if (c is MetroFramework.Controls.MetroLabel || c is MetroFramework.Controls.MetroButton)
                {
                    TextTranslation str = TextTranslations.Where(x => x.Id == c.Name).FirstOrDefault();
                    if (str != null && str.Text.Length > 0)
                    {
                        c.Text = str.Text;
                    }
                    else
                    {
                        if (c.Text != "-")
                        {
                            c.Text = c.Name;
                        }
                    }
                }

                if (c.HasChildren)
                {
                    LoadControlTranslations(c.Controls);
                }
            }
        }
        public static bool IsVCRuntimeInstalled(string arch)
        {
            string subKey = $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}";

            bool Check(RegistryView view)
            {
                var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var key = baseKey.OpenSubKey(subKey);
                if (key == null) return false;

                var installed = key.GetValue("Installed");
                return installed is int i && i == 1;
            }

            // Probamos ambas vistas por si la app es 32-bit o 64-bit
            return Check(RegistryView.Registry64) || Check(RegistryView.Registry32);
        }
    }
    public static class SafeIO
    {
        /// <summary>
        /// Whether the file on disk is missing or holds something other than
        /// <paramref name="data"/>.
        ///
        /// The hook DLLs are written out of the loader resources, and until now
        /// only when the file was absent - so a loader shipping a fixed hook
        /// left every existing install running the old one, forever, and said
        /// "Using existing" in the log while it did. Comparing the bytes is what
        /// makes a new hook actually reach a player who already has the client.
        /// An unreadable file is treated as different so the write is attempted.
        /// </summary>
        public static bool DiffersFrom(string path, byte[] data)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length != data.Length)
                {
                    return true;
                }

                for (int i = 0; i < existing.Length; i++)
                {
                    if (existing[i] != data[i])
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        public static bool TryWriteAllBytes(
            string path,
            byte[] data,
            Action<Exception> onError = null)
        {
            try
            {
                File.WriteAllBytes(path, data);
                return true;
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return false;
            }
        }
    }
}
