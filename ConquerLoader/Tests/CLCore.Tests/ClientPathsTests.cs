using System;
using System.IO;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// The path guard. Ported from the EternalAbyss standalone patcher when the patch step moved inside the loader.
    ///
    /// This is the security boundary of the patcher: the only place where a
    /// string that arrived over the network becomes a file this process writes.
    /// Every rejection below is a path that a compromised or misbuilt manifest
    /// could otherwise use to write outside the client directory, on a machine
    /// belonging to somebody who was invited to play a game.
    /// </summary>
    public sealed class ClientPathsTests
    {
        [Theory]
        [InlineData("ini/itemtype.dat")]
        [InlineData("c3/mesh/800150.c3")]
        [InlineData("ani/ItemMinIcon.Ani")]
        [InlineData("ani/Common.Ani")]
        [InlineData("data/Some File (2).dds")]        // spaces and parentheses are real in this tree
        [InlineData("a")]
        public void Accepts_ordinary_client_paths(string path)
        {
            string problem;
            Assert.True(ClientPaths.IsSafeRelativePath(path, out problem), problem);
            Assert.Null(problem);
        }

        [Theory]
        [InlineData("")]                              // empty
        [InlineData("../secrets.txt")]                // the obvious one
        [InlineData("ini/../../secrets.txt")]         // and buried
        [InlineData("./ini/itemtype.dat")]            // a relative segment either way
        [InlineData("/etc/passwd")]                   // rooted
        [InlineData("C:/Windows/System32/evil.dll")]  // drive letter
        [InlineData("C:evil.dll")]                    // drive-relative, which is not rooted
        [InlineData("ini/itemtype.dat:stream")]       // NTFS alternate data stream
        [InlineData("ini\\itemtype.dat")]             // backslash separator
        [InlineData("ini//itemtype.dat")]             // empty segment
        [InlineData("ini/itemtype.dat ")]             // trailing space Win32 strips
        [InlineData("ini/itemtype.dat.")]             // trailing dot Win32 strips
        public void Rejects_anything_it_cannot_describe_in_one_sentence(string path)
        {
            string problem;
            Assert.False(ClientPaths.IsSafeRelativePath(path, out problem));
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }

        [Fact]
        public void Rejects_a_control_character()
        {
            string problem;
            Assert.False(ClientPaths.IsSafeRelativePath("ini/item" + (char)7 + "type.dat", out problem));
            Assert.Contains("control character", problem);
        }

        // -------------------------------------------------------------------
        // Resolve
        // -------------------------------------------------------------------

        [Fact]
        public void Resolve_lands_inside_the_client_root()
        {
            string root = Path.Combine(Path.GetTempPath(), "eapatch-resolve");
            string full = ClientPaths.Resolve(root, "ini/itemtype.dat");

            Assert.StartsWith(Path.GetFullPath(root), full, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("itemtype.dat", full);
        }

        [Fact]
        public void Resolve_refuses_a_traversal_rather_than_normalising_it()
        {
            string root = Path.Combine(Path.GetTempPath(), "eapatch-resolve");

            // The failure being guarded against is the one where a caller relies
            // on Path.Combine and gets a perfectly valid path somewhere else.
            Assert.Throws<InvalidDataException>(() => ClientPaths.Resolve(root, "../escaped.txt"));
        }

        [Fact]
        public void Resolve_preserves_case_because_the_server_does()
        {
            string root = Path.Combine(Path.GetTempPath(), "eapatch-resolve");
            Assert.EndsWith("Common.Ani", ClientPaths.Resolve(root, "ani/Common.Ani"));
        }

        // -------------------------------------------------------------------
        // FileUrl
        // -------------------------------------------------------------------

        [Fact]
        public void FileUrl_puts_files_under_the_files_prefix()
        {
            var url = ClientPaths.FileUrl(new Uri("https://example.test/client/"), "ini/itemtype.dat");
            Assert.Equal("https://example.test/client/files/ini/itemtype.dat", url.AbsoluteUri);
        }

        [Fact]
        public void FileUrl_escapes_each_segment_but_keeps_the_separators()
        {
            // EscapeDataString over the whole path would turn every '/' into
            // %2F and ask the web server for one very long filename.
            //
            // ASSERTED ON AbsoluteUri, NOT ToString(). Uri.ToString() is a
            // display form that unescapes what it thinks is safe to show - it
            // renders this URL with a literal space in it - while AbsoluteUri is
            // what HttpClient puts on the wire. The first version of this test
            // failed against correct code for exactly that reason.
            var url = ClientPaths.FileUrl(new Uri("https://example.test/client/"), "data/Some File (2).dds");

            Assert.Equal("https://example.test/client/files/data/Some%20File%20%282%29.dds", url.AbsoluteUri);
            Assert.DoesNotContain(" ", url.AbsoluteUri);
        }

        [Fact]
        public void FileUrl_keeps_the_case_the_manifest_gave()
        {
            // A case-sensitive web server serves ani/Common.Ani and ani/common.ani
            // as two different names, and only one of them exists. A URL builder that lowercased the path
            // would 404 on a subset of files that looks random and is not.
            var url = ClientPaths.FileUrl(new Uri("https://example.test/client/"), "ani/Common.Ani");

            Assert.EndsWith("/files/ani/Common.Ani", url.AbsoluteUri);
        }
    }
}
