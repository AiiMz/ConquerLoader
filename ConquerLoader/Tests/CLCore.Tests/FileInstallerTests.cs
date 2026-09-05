using System;
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
            try { Directory.Delete(_root, true); } catch (IOException) { }
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
    }
}
