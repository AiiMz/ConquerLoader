using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CLCore;

namespace ConquerLoader
{
    // Invoked explicitly by artifact verification; never runs during a normal launch.
    internal static class RuntimeSmoke
    {
        internal static int Run(string reportPath)
        {
            string original = Directory.GetCurrentDirectory();
            string scratch = Path.Combine(Path.GetTempPath(), "EternalAbyss-launcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                if (Environment.Version.Major != 10 || Environment.Is64BitProcess)
                    throw new InvalidOperationException("Expected a .NET 10 x86 launcher.");
                string image = CLCore.Patching.SelfUpdate.RunningImagePath();
                if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Self-update did not resolve the running apphost.");
                if (!string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.GetDirectoryName(image), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Plugin base directory must remain beside the launcher, not in bundle extraction storage.");
                string plugin = Path.Combine(Path.GetDirectoryName(image), "Runtime10", "GarmentMotionPlugin.dll");
                Directory.CreateDirectory(Path.Combine(scratch, "Runtime10"));
                File.Copy(plugin, Path.Combine(scratch, "Runtime10", "GarmentMotionPlugin.dll"));
                Directory.SetCurrentDirectory(scratch);

                var app = new System.Windows.Application();
                var main = new Forms.WPF.MainLite();
                main.Close();
                // The retained WinForms UI uses MetroModernUI's Framework assembly.
                // Construct and create its native controls to exercise that compatibility.
                using (var legacy = new Forms.Main())
                {
                    legacy.CreateControl();
                }
                if (!PluginLoader.Plugins.Any(item => item.Name == "Garment motion overrides"))
                    throw new InvalidOperationException("The distributed .NET 10 plugin was not discovered.");
                TestTcpCompatibility();
                app.Shutdown();
                File.WriteAllText(reportPath, "PASS: .NET 10 x86; WPF resources; Metro WinForms; plugin discovery; loopback SimpleTCP; running-image path.");
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(reportPath, error.ToString());
                return 1;
            }
            finally
            {
                Directory.SetCurrentDirectory(original);
                // Only this invocation's scratch directory is removed.
                try { Directory.Delete(scratch, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void TestTcpCompatibility()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using (var received = new ManualResetEventSlim())
            {
                var server = new SimpleTCP.SimpleTcpServer();
                var client = new SimpleTCP.SimpleTcpClient();
                try
                {
                    server.DataReceived += (sender, message) => received.Set();
                    server.Start(IPAddress.Loopback, port);
                    client.Connect("127.0.0.1", port);
                    client.WriteLine("runtime-probe");
                    if (!received.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("SimpleTCP did not deliver the loopback payload.");
                }
                finally
                {
                    client.Disconnect();
                    server.Stop();
                }
            }
        }
    }
}
