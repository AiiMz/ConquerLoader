using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// A patch that replaces a plugin DLL must restart the loader rather than
    /// launch the game: the plugins in memory were loaded when the window opened,
    /// from the files the patch has just replaced.
    ///
    /// End to end through <see cref="ClientPatcher.Run(string, Uri, TimeSpan, Action{string}, Action{int, int}, string)"/>
    /// against a throwaway localhost server. One class, so the tests that set the
    /// process-wide guard variable never run beside each other.
    /// </summary>
    public sealed class PluginReloadTests : IDisposable
    {
        private readonly string _root;
        private readonly TinyServer _server;

        public PluginReloadTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "clpatch-reload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _server = new TinyServer();
            Environment.SetEnvironmentVariable(ClientPatcher.PluginReloadGuardVariable, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ClientPatcher.PluginReloadGuardVariable, null);
            _server.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private string Image { get { return Path.Combine(_root, "EternalAbyss.exe"); } }

        private readonly List<string> _log = new List<string>();

        /// <summary>The patch log, for assertion messages.</summary>
        private string Log { get { return string.Join(Environment.NewLine, _log.ToArray()); } }

        private PatchOutcome Run()
        {
            return ClientPatcher.Run(_root, _server.BaseUrl, TimeSpan.FromSeconds(10), _log.Add, null, Image);
        }

        [Theory]
        [InlineData("Plugins/GarmentMotionPlugin.dll", true)]
        [InlineData("plugins/Remote/Thing.DLL", true)]
        [InlineData("Plugins\\Nested\\A.dll", true)]
        [InlineData("Plugins/plugin-state.json", false)]
        [InlineData("GarmentMotionHook.dll", false)]
        [InlineData("ini/Plugins/x.dll", false)]
        [InlineData("PluginsX/a.dll", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Recognises_plugin_assemblies_by_path(string path, bool expected)
        {
            Assert.Equal(expected, ClientPatcher.IsPluginAssembly(path));
        }

        [Fact]
        public void An_updated_plugin_asks_for_a_restart_and_not_a_launch()
        {
            WriteLocal("Plugins/GarmentMotionPlugin.dll", "old plugin");
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "new plugin");

            PatchOutcome outcome = Run();

            Assert.True(outcome.PluginReloadRequired, Log);
            Assert.Equal(Image, outcome.RelaunchImagePath);
            Assert.Equal("new plugin", ReadLocal("Plugins/GarmentMotionPlugin.dll"));
        }

        [Fact]
        public void An_updated_plugin_reports_the_required_restart_in_live_status()
        {
            WriteLocal("Plugins/GarmentMotionPlugin.dll", "old plugin");
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "new plugin");
            var statuses = new List<string>();

            PatchOutcome outcome = ClientPatcher.Run(_root, _server.BaseUrl,
                TimeSpan.FromSeconds(10), _log.Add, null, Image, statuses.Add);

            Assert.True(outcome.PluginReloadRequired, Log);
            Assert.Equal("Updates installed. Restarting the launcher to reload plugins...",
                statuses[statuses.Count - 1]);
        }

        [Fact]
        public void A_missing_plugin_restored_also_asks_for_a_restart()
        {
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "plugin");

            Assert.True(Run().PluginReloadRequired, Log);
        }

        [Fact]
        public void Other_files_do_not_restart_the_loader()
        {
            WriteLocal("GarmentMotionHook.dll", "old hook");
            _server.Publish("GarmentMotionHook.dll", "new hook");
            _server.Publish("Plugins/plugin-state.json", "{}");

            PatchOutcome outcome = Run();

            Assert.True(outcome.CanLaunch, Log);
            Assert.True(outcome.ChangedAnything, Log);
            Assert.False(outcome.PluginReloadRequired);
        }

        [Fact]
        public void An_up_to_date_plugin_does_not_restart_the_loader()
        {
            // The restarted loader runs the patch again and must find nothing to
            // do, or it would restart forever.
            WriteLocal("Plugins/GarmentMotionPlugin.dll", "plugin");
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "plugin");

            PatchOutcome outcome = Run();

            Assert.True(outcome.CanLaunch);
            Assert.False(outcome.PluginReloadRequired);
        }

        [Fact]
        public void A_second_reload_in_a_row_refuses_rather_than_restarting_again()
        {
            Environment.SetEnvironmentVariable(ClientPatcher.PluginReloadGuardVariable, "1");
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "plugin");

            PatchOutcome outcome = Run();

            Assert.False(outcome.PluginReloadRequired);
            Assert.False(outcome.CanLaunch);
            Assert.False(string.IsNullOrEmpty(outcome.Message));
        }

        [Fact]
        public void A_self_update_restart_does_not_count_against_the_plugin_reload()
        {
            // A release changing the loader AND a plugin restarts once for each.
            Assert.NotEqual(SelfUpdate.GuardVariable, ClientPatcher.PluginReloadGuardVariable);
        }

        [Fact]
        public void Without_a_known_image_it_refuses_instead_of_launching_stale_plugins()
        {
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "plugin");

            PatchOutcome outcome = ClientPatcher.Run(_root, _server.BaseUrl, TimeSpan.FromSeconds(10), null, null, null);

            Assert.False(outcome.PluginReloadRequired);
            Assert.False(outcome.CanLaunch);
        }

        [Fact]
        public void A_failed_file_reports_the_failure_rather_than_a_restart()
        {
            _server.Publish("Plugins/GarmentMotionPlugin.dll", "plugin");
            _server.Publish("ini/broken.dat", "promised", "delivered");

            PatchOutcome outcome = Run();

            Assert.False(outcome.CanLaunch);
            Assert.False(outcome.PluginReloadRequired);
        }

        private void WriteLocal(string relative, string content)
        {
            string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private string ReadLocal(string relative)
        {
            return File.ReadAllText(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>
        /// Just enough HTTP/1.1 to serve a manifest and its files. TcpListener
        /// rather than HttpListener, which needs a URL ACL without admin rights.
        /// </summary>
        private sealed class TinyServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private readonly List<ManifestFile> _entries = new List<ManifestFile>();
            private volatile bool _stopped;

            public TinyServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                BaseUrl = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            public Uri BaseUrl { get; private set; }

            public void Publish(string path, string content)
            {
                Publish(path, content, content);
            }

            /// <summary>Lists <paramref name="promised"/> in the manifest and serves <paramref name="served"/>.</summary>
            public void Publish(string path, string promised, string served)
            {
                byte[] promisedBytes = Encoding.UTF8.GetBytes(promised);
                using (SHA256 sha = SHA256.Create())
                    _entries.Add(new ManifestFile { Path = path, Size = promisedBytes.Length, Sha256 = PatchPlanner.ToHex(sha.ComputeHash(promisedBytes)) });

                lock (_files) _files[path] = Encoding.UTF8.GetBytes(served);
            }

            private byte[] Manifest()
            {
                long total = 0;
                foreach (ManifestFile e in _entries) total += e.Size;

                var json = new StringBuilder();
                json.Append("{\"schemaVersion\":").Append(CLCore.Patching.Manifest.SupportedSchemaVersion)
                    .Append(",\"version\":\"test\",\"fileCount\":").Append(_entries.Count)
                    .Append(",\"totalBytes\":").Append(total).Append(",\"files\":[");

                for (int i = 0; i < _entries.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append("{\"path\":\"").Append(_entries[i].Path.Replace("\\", "\\\\"))
                        .Append("\",\"size\":").Append(_entries[i].Size)
                        .Append(",\"sha256\":\"").Append(_entries[i].Sha256).Append("\"}");
                }

                json.Append("]}");
                return Encoding.UTF8.GetBytes(json.ToString());
            }

            private void Serve()
            {
                while (!_stopped)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }

                    try
                    {
                        using (client)
                        using (NetworkStream stream = client.GetStream())
                        {
                            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                            string requestLine = reader.ReadLine() ?? string.Empty;
                            string line;
                            while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }

                            string[] parts = requestLine.Split(' ');
                            string path = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].TrimStart('/')) : string.Empty;

                            byte[] body = null;
                            if (path == "manifest.json") body = Manifest();
                            else if (path.StartsWith("files/", StringComparison.Ordinal))
                                lock (_files) _files.TryGetValue(path.Substring("files/".Length), out body);

                            string status = body == null ? "404 Not Found" : "200 OK";
                            body = body ?? new byte[0];

                            byte[] head = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 " + status + "\r\nContent-Length: " + body.Length
                                + "\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                            stream.Write(head, 0, head.Length);
                            stream.Write(body, 0, body.Length);
                        }
                    }
                    catch (IOException) { }
                }
            }

            public void Dispose()
            {
                _stopped = true;
                _listener.Stop();
            }
        }
    }
}
