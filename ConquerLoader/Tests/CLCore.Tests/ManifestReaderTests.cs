using System.IO;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// Manifest validation. Ported from the EternalAbyss standalone patcher when the patch step moved inside the loader.
    ///
    /// Everything downstream trusts this document, so the interesting tests are
    /// the rejections. A manifest that is wrong in a way this class accepts
    /// becomes a client that is wrong in a way nobody can see.
    /// </summary>
    public sealed class ManifestReaderTests
    {
        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static string Document(string files, int fileCount, long totalBytes, int schemaVersion = 1)
        {
            return "{\"schemaVersion\":" + schemaVersion +
                   ",\"version\":\"283ccf56ca1b9814\"" +
                   ",\"fileCount\":" + fileCount +
                   ",\"totalBytes\":" + totalBytes +
                   ",\"files\":[" + files + "]}";
        }

        private static string Entry(string path, long size, string hash)
        {
            return "{\"path\":\"" + path + "\",\"size\":" + size + ",\"sha256\":\"" + hash + "\"}";
        }

        [Fact]
        public void Reads_a_well_formed_manifest()
        {
            Manifest m = ManifestReader.Parse(Document(
                Entry("ini/itemtype.dat", 10, HashA) + "," + Entry("ani/Common.Ani", 20, HashB), 2, 30));

            Assert.Equal(1, m.SchemaVersion);
            Assert.Equal("283ccf56ca1b9814", m.Version);
            Assert.Equal(2, m.Files.Count);
            Assert.Equal("ini/itemtype.dat", m.Files[0].Path);
        }

        [Fact]
        public void Refuses_a_schema_version_it_does_not_know()
        {
            // The whole point of the field: an older patcher must stop rather
            // than skip fields it cannot see.
            var ex = Assert.Throws<InvalidDataException>(() =>
                ManifestReader.Parse(Document(Entry("a", 1, HashA), 1, 1, schemaVersion: 2)));

            Assert.Contains("schemaVersion", ex.Message);
            Assert.Contains("Download the current client", ex.Message);
        }

        [Fact]
        public void Refuses_a_file_count_that_disagrees_with_the_list()
        {
            // A manifest truncated in transit parses cleanly and describes a
            // client missing files. Without this it would read as "nothing to do".
            var ex = Assert.Throws<InvalidDataException>(() =>
                ManifestReader.Parse(Document(Entry("a", 1, HashA), 900, 1)));

            Assert.Contains("incomplete", ex.Message);
        }

        [Fact]
        public void Refuses_a_total_that_disagrees_with_the_entries()
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                ManifestReader.Parse(Document(Entry("a", 1, HashA), 1, 999)));

            Assert.Contains("inconsistent with itself", ex.Message);
        }

        [Fact]
        public void Refuses_a_path_that_escapes_the_client_directory()
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                ManifestReader.Parse(Document(Entry("../../evil.dll", 1, HashA), 1, 1)));

            Assert.Contains("not a path this loader will write", ex.Message);
        }

        [Fact]
        public void Refuses_the_same_path_twice()
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                ManifestReader.Parse(Document(
                    Entry("ini/itemtype.dat", 1, HashA) + "," + Entry("ini/itemtype.dat", 2, HashB), 2, 3)));

            Assert.Contains("twice", ex.Message);
        }

        [Fact]
        public void Accepts_two_paths_that_differ_only_in_case()
        {
            // They are two different files to a case-sensitive web server, and a
            // Conquer tree really does ship Common.Ani next to ChatEmotion.ani. Collapsing
            // them here would silently drop one.
            Manifest m = ManifestReader.Parse(Document(
                Entry("ani/common.ani", 1, HashA) + "," + Entry("ani/Common.Ani", 2, HashB), 2, 3));

            Assert.Equal(2, m.Files.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("nothex")]
        [InlineData("aaaa")]
        public void Refuses_an_entry_without_a_real_hash(string hash)
        {
            // Comparing against a hash that cannot match means re-downloading
            // every file on every run: slow, silent, and shaped like bandwidth
            // rather than like an error.
            Assert.Throws<InvalidDataException>(() =>
                ManifestReader.Parse(Document(Entry("a", 1, hash), 1, 1)));
        }

        [Fact]
        public void Refuses_html_pretending_to_be_a_manifest()
        {
            // A captive portal answers every request with a login page and a 200.
            var ex = Assert.Throws<InvalidDataException>(() => ManifestReader.Parse("<html><body>Sign in</body></html>"));
            Assert.Contains("not valid JSON", ex.Message);
        }

        [Fact]
        public void Refuses_an_empty_body()
        {
            Assert.Throws<InvalidDataException>(() => ManifestReader.Parse("   "));
        }
    }
}
