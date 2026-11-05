using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// Installing a downloaded file. Ported from the EternalAbyss standalone patcher when the patch step moved inside the loader.
    ///
    /// The property worth protecting: A FAILED PATCH LEAVES THE INSTALL EXACTLY
    /// AS IT WAS. Every test here is a way that could stop being true.
    /// </summary>
    public sealed class FileInstallerTests : IDisposable
    {
        private readonly string _root;

        public FileInstallerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "clpatch-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            // A DLL the locked-file test loaded stays mapped until the test host exits.
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static byte[] Bytes(string s)
        {
            return new UTF8Encoding(false).GetBytes(s);
        }

        private static string Sha256Of(byte[] data)
        {
            using (var sha = SHA256.Create())
                return PatchPlanner.ToHex(sha.ComputeHash(data));
        }

        private static ManifestFile EntryFor(string path, byte[] content)
        {
            return new ManifestFile { Path = path, Size = content.Length, Sha256 = Sha256Of(content) };
        }

        private string PathOf(string relative)
        {
            return Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        [Fact]
        public void Reports_streamed_bytes_and_verification_before_installing()
        {
            byte[] content = new byte[200000];
            ManifestFile entry = EntryFor("ini/progress.dat", content);
            var received = new List<long>();
            var stages = new List<string>();

            FileInstaller.Install(_root, entry, new MemoryStream(content), received.Add, message =>
            {
                // A phase announcement must not claim that unverified bytes are installed.
                Assert.False(File.Exists(PathOf(entry.Path)));
                stages.Add(message);
            });

            Assert.Equal(0, received.First());
            Assert.Equal(content.Length, received.Last());
            Assert.True(received.SequenceEqual(received.OrderBy(value => value)));
            Assert.Equal(new[] { "Verifying: " + entry.Path, "Installing: " + entry.Path }, stages);
            Assert.Equal(content, File.ReadAllBytes(PathOf(entry.Path)));
            Assert.False(File.Exists(PathOf(entry.Path) + FileInstaller.PartialSuffix));
        }

        [Fact]
        public void Reporting_progress_does_not_install_a_corrupt_download()
        {
            byte[] promised = Bytes("new content");
            byte[] original = Bytes("original");
            ManifestFile entry = EntryFor("progress.dat", promised);
            File.WriteAllBytes(PathOf(entry.Path), original);
            var stages = new List<string>();
            long received = -1;

            Assert.Throws<InvalidDataException>(() => FileInstaller.Install(_root, entry,
                new MemoryStream(Bytes("bad content")), count => received = count, stages.Add));

            Assert.Equal(promised.Length, received);
            Assert.Equal(new[] { "Verifying: " + entry.Path }, stages);
            Assert.Equal(original, File.ReadAllBytes(PathOf(entry.Path)));
            Assert.False(File.Exists(PathOf(entry.Path) + FileInstaller.PartialSuffix));
        }

        [Fact]
        public void Writes_the_file_and_creates_its_directory()
        {
            // New content lands in directories that do not exist yet - a new
            // garment's mesh folder is the routine case.
            byte[] content = Bytes("mesh bytes");
            ManifestFile entry = EntryFor("c3/mesh/new/800150.c3", content);

            FileInstaller.Install(_root, entry, new MemoryStream(content));

            Assert.Equal(content, File.ReadAllBytes(PathOf(entry.Path)));
        }

        [Fact]
        public void Replaces_an_existing_file()
        {
            byte[] updated = Bytes("new content");
            ManifestFile entry = EntryFor("ini/a.dat", updated);

            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(entry.Path)));
            File.WriteAllBytes(PathOf(entry.Path), Bytes("old content"));

            FileInstaller.Install(_root, entry, new MemoryStream(updated));

            Assert.Equal(updated, File.ReadAllBytes(PathOf(entry.Path)));
        }

        [Fact]
        public void A_hash_mismatch_leaves_the_existing_file_untouched()
        {
            // The one that matters. A server serving the wrong bytes, or a proxy
            // rewriting them, must not be able to damage a working install.
            byte[] promised = Bytes("what the manifest says");
            byte[] delivered = Bytes("what the server sent!!");
            Assert.Equal(promised.Length, delivered.Length);   // same size, different bytes

            ManifestFile entry = EntryFor("ini/a.dat", promised);

            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(entry.Path)));
            File.WriteAllBytes(PathOf(entry.Path), Bytes("the original"));

            Assert.Throws<InvalidDataException>(() => FileInstaller.Install(_root, entry, new MemoryStream(delivered)));

            Assert.Equal(Bytes("the original"), File.ReadAllBytes(PathOf(entry.Path)));
        }

        [Fact]
        public void A_short_read_is_rejected_by_size_with_its_own_message()
        {
            byte[] promised = Bytes("the whole file");
            ManifestFile entry = EntryFor("ini/a.dat", promised);

            var ex = Assert.Throws<InvalidDataException>(
                () => FileInstaller.Install(_root, entry, new MemoryStream(Bytes("the wh"))));

            Assert.Contains("bytes, received", ex.Message);
            Assert.False(File.Exists(PathOf(entry.Path)));
        }

        [Fact]
        public void A_failed_install_leaves_no_partial_file_behind()
        {
            byte[] promised = Bytes("aaaa");
            ManifestFile entry = EntryFor("ini/a.dat", promised);

            Assert.Throws<InvalidDataException>(() => FileInstaller.Install(_root, entry, new MemoryStream(Bytes("bbbb"))));

            string[] leftovers = Directory
                .GetFiles(_root, "*" + FileInstaller.PartialSuffix, SearchOption.AllDirectories);

            Assert.Empty(leftovers);
        }

        [Fact]
        public void A_successful_install_leaves_no_partial_file_behind()
        {
            byte[] content = Bytes("fine");
            ManifestFile entry = EntryFor("ini/a.dat", content);

            FileInstaller.Install(_root, entry, new MemoryStream(content));

            Assert.Empty(Directory.GetFiles(_root, "*" + FileInstaller.PartialSuffix, SearchOption.AllDirectories));
        }

        [Fact]
        public void Refuses_to_install_outside_the_client_directory()
        {
            byte[] content = Bytes("evil");
            var entry = new ManifestFile { Path = "../escaped.dll", Size = content.Length, Sha256 = Sha256Of(content) };

            Assert.Throws<InvalidDataException>(() => FileInstaller.Install(_root, entry, new MemoryStream(content)));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "escaped.dll")));
        }

        [Fact]
        public void Zero_byte_files_install()
        {
            // The manifest lists whatever the generator tracks, and an empty file is a file
            // like any other. A CryptoStream with nothing written to it is the
            // kind of edge that only shows up on somebody else's machine.
            byte[] empty = Array.Empty<byte>();
            ManifestFile entry = EntryFor("ini/empty.dat", empty);

            FileInstaller.Install(_root, entry, new MemoryStream(empty));

            Assert.True(File.Exists(PathOf(entry.Path)));
            Assert.Equal(0, new FileInfo(PathOf(entry.Path)).Length);
        }

        #region A destination that is in use

        [Fact]
        public void Replaces_a_dll_that_is_loaded_by_a_running_process()
        {
            // The GarmentMotionPlugin.dll report: the loader had LoadFile'd the
            // plugin, File.Delete on it was "access denied", and the whole patch
            // stopped. A mapped image cannot be deleted but can be renamed, so the
            // install must move it aside and still put the new bytes in place.
            string plugin = PathOf("Plugins/Locked.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(plugin));
            File.Copy(typeof(FileInstallerTests).Assembly.Location, plugin);
            System.Reflection.Assembly.LoadFile(plugin);   // maps it for the life of this process

            Assert.ThrowsAny<Exception>(() => File.Delete(plugin));   // the original failure, still real

            byte[] updated = Bytes("the new plugin");
            ManifestFile entry = EntryFor("Plugins/Locked.dll", updated);

            FileInstaller.Install(_root, entry, new MemoryStream(updated));

            Assert.Equal(updated, File.ReadAllBytes(plugin));
            Assert.True(File.Exists(plugin + FileInstaller.MovedAsideSuffix));
        }

        [Fact]
        public void Replaces_a_read_only_file()
        {
            // The other route to UnauthorizedAccessException from File.Delete.
            byte[] updated = Bytes("new");
            ManifestFile entry = EntryFor("ini/ro.dat", updated);
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(entry.Path)));
            File.WriteAllBytes(PathOf(entry.Path), Bytes("old"));
            File.SetAttributes(PathOf(entry.Path), FileAttributes.ReadOnly);

            FileInstaller.Install(_root, entry, new MemoryStream(updated));

            Assert.Equal(updated, File.ReadAllBytes(PathOf(entry.Path)));
            string aside = PathOf(entry.Path) + FileInstaller.MovedAsideSuffix;
            if (File.Exists(aside)) File.SetAttributes(aside, FileAttributes.Normal);
        }

        [Fact]
        public void A_later_install_sweeps_what_an_earlier_one_moved_aside()
        {
            byte[] content = Bytes("v3");
            ManifestFile entry = EntryFor("ini/a.dat", content);
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(entry.Path)));
            File.WriteAllBytes(PathOf(entry.Path), Bytes("v2"));
            File.WriteAllBytes(PathOf(entry.Path) + FileInstaller.MovedAsideSuffix, Bytes("v1"));
            File.WriteAllBytes(PathOf(entry.Path) + ".2" + FileInstaller.MovedAsideSuffix, Bytes("v0"));

            FileInstaller.Install(_root, entry, new MemoryStream(content));

            Assert.False(File.Exists(PathOf(entry.Path) + FileInstaller.MovedAsideSuffix));
            Assert.False(File.Exists(PathOf(entry.Path) + ".2" + FileInstaller.MovedAsideSuffix));
        }

        [Fact]
        public void The_sweep_leaves_a_players_own_old_backup_alone()
        {
            byte[] content = Bytes("new exe");
            ManifestFile entry = EntryFor("Conquer.exe", content);
            File.WriteAllBytes(PathOf("Conquer.exe"), Bytes("old exe"));
            File.WriteAllBytes(PathOf("Conquer.exe.old"), Bytes("their backup"));

            FileInstaller.Install(_root, entry, new MemoryStream(content));

            Assert.Equal(Bytes("their backup"), File.ReadAllBytes(PathOf("Conquer.exe.old")));
        }

        [Fact]
        public void The_moved_aside_name_ends_in_old_so_the_manifest_and_archive_exclude_it()
        {
            Assert.EndsWith(".old", FileInstaller.MovedAsideSuffix, StringComparison.Ordinal);
        }

        #endregion

        #region Stage - the half SelfUpdate uses

        [Fact]
        public void Stage_writes_the_verified_bytes_and_leaves_them_for_the_caller()
        {
            // The loader replacing itself needs the new bytes on disk, verified,
            // but cannot let anything move them into place - a running image can
            // be renamed and not overwritten. Issue #53.
            byte[] content = Bytes("new loader");
            ManifestFile entry = EntryFor("ConquerLoader.exe", content);

            string staged = FileInstaller.Stage(_root, entry, new MemoryStream(content));

            Assert.True(File.Exists(staged));
            Assert.Equal(content, File.ReadAllBytes(staged));
            Assert.EndsWith(FileInstaller.PartialSuffix, staged, StringComparison.Ordinal);
        }

        [Fact]
        public void Stage_does_not_touch_the_destination()
        {
            byte[] existing = Bytes("old loader");
            byte[] content = Bytes("new loader");
            ManifestFile entry = EntryFor("ConquerLoader.exe", content);

            File.WriteAllBytes(PathOf(entry.Path), existing);

            FileInstaller.Stage(_root, entry, new MemoryStream(content));

            Assert.Equal(existing, File.ReadAllBytes(PathOf(entry.Path)));
        }

        [Fact]
        public void Stage_verifies_by_the_same_rule_as_Install_and_keeps_nothing_that_fails()
        {
            // The point of sharing this half rather than writing a second one:
            // bytes that do not match the manifest never become a file anybody
            // can run, and that is worth more on an executable than anywhere else.
            byte[] content = Bytes("new loader");
            ManifestFile entry = EntryFor("ConquerLoader.exe", content);
            entry.Sha256 = new string('0', 64);

            Assert.Throws<InvalidDataException>(
                () => FileInstaller.Stage(_root, entry, new MemoryStream(content)));

            Assert.False(File.Exists(PathOf(entry.Path) + FileInstaller.PartialSuffix));
            Assert.False(File.Exists(PathOf(entry.Path)));
        }

        [Fact]
        public void Stage_rejects_a_short_download()
        {
            byte[] content = Bytes("new loader");
            ManifestFile entry = EntryFor("ConquerLoader.exe", content);
            entry.Size = content.Length + 1;

            Assert.Throws<InvalidDataException>(
                () => FileInstaller.Stage(_root, entry, new MemoryStream(content)));

            Assert.False(File.Exists(PathOf(entry.Path) + FileInstaller.PartialSuffix));
        }

        [Fact]
        public void Stage_refuses_a_path_that_escapes_the_client_root()
        {
            // Stage is reachable from the network like Install is, so it gets the
            // path guard for the same reason rather than by inheritance.
            byte[] content = Bytes("payload");
            ManifestFile entry = EntryFor("../escaped.dll", content);

            Assert.Throws<InvalidDataException>(
                () => FileInstaller.Stage(_root, entry, new MemoryStream(content)));
        }

        #endregion
    }
}
