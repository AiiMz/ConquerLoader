using System;
using System.IO;
using System.Text;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// The loader replacing itself. Issue #53.
    ///
    /// The property worth protecting is the same one FileInstallerTests protects,
    /// with one addition that is worse than the others: A FAILED SELF-UPDATE
    /// LEAVES A WORKING LOADER AT ITS OWN NAME. Every other file in the patch
    /// layer is inert data, and the failure mode is a missing texture. This one
    /// can leave a player with nothing to double-click, and their only repair is
    /// a 1.4 GB download.
    ///
    /// The running image is simulated by an ordinary file: nothing here needs the
    /// process to actually be running from it, because what is under test is the
    /// rename dance and the naming rules, not the operating system's willingness
    /// to rename a mapped image. That willingness is what the rehearsal against a
    /// real install covers.
    /// </summary>
    public sealed class SelfUpdateTests : IDisposable
    {
        private readonly string _root;

        public SelfUpdateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "clpatch-self-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string Write(string relative, string content)
        {
            string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return full;
        }

        #region IsRunningImage

        [Fact]
        public void The_entry_that_resolves_to_the_running_image_is_recognised()
        {
            string image = Write("ConquerLoader.exe", "v1");

            Assert.True(SelfUpdate.IsRunningImage(_root, "ConquerLoader.exe", image));
        }

        [Fact]
        public void A_different_entry_in_the_same_root_is_not_the_running_image()
        {
            string image = Write("ConquerLoader.exe", "v1");

            Assert.False(SelfUpdate.IsRunningImage(_root, "Conquer.exe", image));
            Assert.False(SelfUpdate.IsRunningImage(_root, "ini/itemtype.dat", image));
        }

        [Fact]
        public void An_image_outside_the_client_root_never_matches()
        {
            // The developer case, and the reason this compares resolved paths
            // rather than file names: a loader run from bin\Release against some
            // other tree must not rename itself into that tree.
            string elsewhere = Path.Combine(Path.GetTempPath(), "clpatch-elsewhere-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(elsewhere);

            try
            {
                string image = Path.Combine(elsewhere, "ConquerLoader.exe");
                File.WriteAllText(image, "v1");

                Assert.False(SelfUpdate.IsRunningImage(_root, "ConquerLoader.exe", image));
            }
            finally
            {
                try { Directory.Delete(elsewhere, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void Case_differences_in_the_entry_still_match()
        {
            // Manifest paths keep the generator's case and Windows does not care.
            // Answering "no" here would download a second loader beside the first.
            string image = Write("ConquerLoader.exe", "v1");

            Assert.True(SelfUpdate.IsRunningImage(_root, "conquerloader.EXE", image));
        }

        [Fact]
        public void An_unsafe_manifest_path_is_answered_rather_than_thrown()
        {
            // Staying a predicate matters: the loud refusal belongs to the normal
            // path, which validates the whole document. If this threw, a hostile
            // manifest would fail here instead - in a different place, with a
            // different message, before the guard that exists to say it.
            string image = Write("ConquerLoader.exe", "v1");

            Assert.False(SelfUpdate.IsRunningImage(_root, "../ConquerLoader.exe", image));
            Assert.False(SelfUpdate.IsRunningImage(_root, "C:/Windows/System32/cmd.exe", image));
        }

        [Fact]
        public void No_running_image_means_no_match()
        {
            // Null is how every caller that is not the launch path turns the whole
            // feature off, so it has to be a quiet no rather than an exception.
            Assert.False(SelfUpdate.IsRunningImage(_root, "ConquerLoader.exe", null));
            Assert.False(SelfUpdate.IsRunningImage(_root, "ConquerLoader.exe", ""));
        }

        #endregion

        #region Replace

        [Fact]
        public void Replace_moves_the_old_image_aside_and_the_new_one_into_its_name()
        {
            string image = Write("ConquerLoader.exe", "old loader");
            string staged = Write("ConquerLoader.exe.eapatch-part", "new loader");

            string superseded = SelfUpdate.Replace(image, staged);

            Assert.Equal("new loader", File.ReadAllText(image));
            Assert.Equal("old loader", File.ReadAllText(superseded));
            Assert.False(File.Exists(staged));
        }

        [Fact]
        public void The_superseded_name_ends_in_old_so_the_existing_backup_rules_catch_it()
        {
            // Both EternalAbyssCo's ManifestBuilder.IsBackup and
            // New-ClientArchive.ps1 exclude "*.old" already. That reuse is the
            // whole reason for this suffix, and it is only true while the suffix
            // is last.
            string image = Write("ConquerLoader.exe", "old loader");
            string staged = Write("ConquerLoader.exe.eapatch-part", "new loader");

            string superseded = SelfUpdate.Replace(image, staged);

            Assert.EndsWith(".old", superseded, StringComparison.Ordinal);
        }

        [Fact]
        public void A_previous_superseded_image_is_cleared_and_its_name_reused()
        {
            string image = Write("ConquerLoader.exe", "v2");
            Write("ConquerLoader.exe.old", "v1");
            string staged = Write("ConquerLoader.exe.eapatch-part", "v3");

            string superseded = SelfUpdate.Replace(image, staged);

            Assert.Equal(Path.Combine(_root, "ConquerLoader.exe.old"), superseded);
            Assert.Equal("v2", File.ReadAllText(superseded));
            Assert.Equal("v3", File.ReadAllText(image));
        }

        [Fact]
        public void A_locked_superseded_image_is_stepped_over_rather_than_failed_on()
        {
            // The previous image is still mapped by a loader that has not exited,
            // or held open by a scanner. Refusing to update in that case would
            // make the update depend on something outside this program's control.
            string image = Write("ConquerLoader.exe", "v2");
            string locked = Write("ConquerLoader.exe.old", "v1");
            string staged = Write("ConquerLoader.exe.eapatch-part", "v3");

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                string superseded = SelfUpdate.Replace(image, staged);

                Assert.Equal(Path.Combine(_root, "ConquerLoader.exe.2.old"), superseded);
                Assert.Equal("v3", File.ReadAllText(image));
            }
        }

        [Fact]
        public void The_numbered_fallback_still_ends_in_old()
        {
            string image = Write("ConquerLoader.exe", "v2");
            string locked = Write("ConquerLoader.exe.old", "v1");
            string staged = Write("ConquerLoader.exe.eapatch-part", "v3");

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                string superseded = SelfUpdate.Replace(image, staged);

                Assert.True(SelfUpdate.IsSupersededImage("ConquerLoader.exe", Path.GetFileName(superseded)));
                Assert.EndsWith(".old", superseded, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_missing_staged_file_leaves_the_loader_exactly_where_it_was()
        {
            string image = Write("ConquerLoader.exe", "old loader");

            Assert.Throws<FileNotFoundException>(
                () => SelfUpdate.Replace(image, Path.Combine(_root, "nothing-here.part")));

            Assert.Equal("old loader", File.ReadAllText(image));
            Assert.False(File.Exists(image + ".old"));
        }

        #endregion

        #region IsSupersededImage

        [Theory]
        [InlineData("ConquerLoader.exe.old", true)]
        [InlineData("ConquerLoader.exe.2.old", true)]
        [InlineData("ConquerLoader.exe.17.old", true)]
        [InlineData("CONQUERLOADER.EXE.OLD", true)]
        [InlineData("ConquerLoader.exe", false)]
        [InlineData("ConquerLoader.exe.old.old", false)]
        [InlineData("ConquerLoader.exeX.old", false)]
        [InlineData("ConquerLoader.exe.bak.old", false)]
        [InlineData("Conquer.exe.old", false)]
        [InlineData("notes.old", false)]
        public void Only_the_names_this_class_produces_are_recognised(string candidate, bool expected)
        {
            Assert.Equal(expected, SelfUpdate.IsSupersededImage("ConquerLoader.exe", candidate));
        }

        #endregion

        #region SweepSuperseded

        [Fact]
        public void The_sweep_removes_images_left_by_previous_updates()
        {
            string image = Write("ConquerLoader.exe", "v3");
            Write("ConquerLoader.exe.old", "v2");
            Write("ConquerLoader.exe.2.old", "v1");

            int removed = SelfUpdate.SweepSuperseded(image, null);

            Assert.Equal(2, removed);
            Assert.False(File.Exists(Path.Combine(_root, "ConquerLoader.exe.old")));
            Assert.False(File.Exists(Path.Combine(_root, "ConquerLoader.exe.2.old")));
            Assert.True(File.Exists(image));
        }

        [Fact]
        public void The_sweep_leaves_alone_every_old_file_that_is_not_ours()
        {
            // A patcher that deleted "*.old" beside itself would be deleting a
            // player's files because they happened to name one the way it names
            // its own. The client root is somebody's folder, not this program's.
            string image = Write("ConquerLoader.exe", "v2");
            Write("ConquerLoader.exe.old", "v1");
            Write("config.json.old", "their backup");
            Write("Conquer.exe.old", "their backup");
            Write("ConquerLoader.exe.bak", "their backup");

            SelfUpdate.SweepSuperseded(image, null);

            Assert.False(File.Exists(Path.Combine(_root, "ConquerLoader.exe.old")));
            Assert.True(File.Exists(Path.Combine(_root, "config.json.old")));
            Assert.True(File.Exists(Path.Combine(_root, "Conquer.exe.old")));
            Assert.True(File.Exists(Path.Combine(_root, "ConquerLoader.exe.bak")));
        }

        [Fact]
        public void A_superseded_image_that_cannot_be_deleted_is_reported_and_not_an_error()
        {
            // It is still mapped by the process that is doing the sweeping, which
            // is exactly the state the first launch after an update is in.
            string image = Write("ConquerLoader.exe", "v2");
            string locked = Write("ConquerLoader.exe.old", "v1");

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                int removed = SelfUpdate.SweepSuperseded(image, null);

                Assert.Equal(0, removed);
                Assert.True(File.Exists(locked));
            }
        }

        [Fact]
        public void Sweeping_without_a_running_image_does_nothing()
        {
            Assert.Equal(0, SelfUpdate.SweepSuperseded(null, null));
            Assert.Equal(0, SelfUpdate.SweepSuperseded("", null));
        }

        #endregion

        #region QuoteArguments

        [Theory]
        [InlineData(new string[0], "")]
        [InlineData(new[] { "-quiet" }, "-quiet")]
        [InlineData(new[] { "-quiet", "-dx9" }, "-quiet -dx9")]
        public void Arguments_are_passed_on_in_a_form_the_new_process_can_parse(string[] arguments, string expected)
        {
            Assert.Equal(expected, SelfUpdate.QuoteArguments(arguments));
        }

        [Theory]
        // A BACKSLASH IS NOT AN ESCAPE unless a run of them is immediately
        // followed by a quote. Doubling them all is the version that reads
        // correctly and hands the restarted loader a path with doubled
        // separators - which is exactly what a rehearsal against a real
        // restarted process produced before this was fixed.
        [InlineData("D:\\Some Dir\\Conquer", "\"D:\\Some Dir\\Conquer\"")]
        [InlineData("D:\\NoSpaces\\Conquer", "D:\\NoSpaces\\Conquer")]
        // A trailing run sits against the closing quote, so it IS in the
        // escaping position - without doubling, the quote is escaped instead of
        // closing the argument and everything after it joins on.
        [InlineData("D:\\Some Dir\\", "\"D:\\Some Dir\\\\\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [InlineData("", "\"\"")]
        public void Backslashes_only_escape_where_Windows_says_they_do(string argument, string expected)
        {
            Assert.Equal(expected, SelfUpdate.QuoteArguments(new[] { argument }));
        }

        [Fact]
        public void A_null_argument_list_is_an_empty_command_line()
        {
            Assert.Equal(string.Empty, SelfUpdate.QuoteArguments(null));
        }

        #endregion
    }
}
