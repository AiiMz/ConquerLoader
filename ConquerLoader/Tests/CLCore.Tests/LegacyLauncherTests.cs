using System;
using System.IO;
using System.Text;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// The old launcher name stepping aside for the new one.
    ///
    /// THE PROPERTY WORTH PROTECTING is that this never costs anybody a launcher.
    /// It runs before any window exists, on every start, on machines nobody can
    /// look at - so every question it asks has to answer "carry on as this
    /// process" unless it is certain, and "certain" means a real, non-empty
    /// sibling under the new name that is not this image.
    ///
    /// The running image is simulated by an ordinary file, exactly as in
    /// SelfUpdateTests: what is under test is the naming rule, not the operating
    /// system. Process.Start is not exercised here - <see cref="LegacyLauncher
    /// .HandOff"/> is two lines of ProcessStartInfo and the loader's caller
    /// treats a throw from it as "carry on".
    /// </summary>
    public sealed class LegacyLauncherTests : IDisposable
    {
        private readonly string _root;

        public LegacyLauncherTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "clpatch-legacy-" + Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void The_old_name_hands_off_to_the_new_one_beside_it()
        {
            string image = Write(LegacyLauncher.LegacyName, "the same build, legacy copy");
            string expected = Write(LegacyLauncher.CurrentName, "the same build");

            Assert.Equal(expected, LegacyLauncher.ResolveHandOff(image));
        }

        [Fact]
        public void The_name_is_matched_however_it_is_spelled()
        {
            Write(LegacyLauncher.CurrentName, "the same build");
            string image = Path.Combine(_root, "conquerloader.EXE");
            File.WriteAllText(image, "legacy copy");

            Assert.NotNull(LegacyLauncher.ResolveHandOff(image));
        }

        [Fact]
        public void The_new_name_does_not_hand_off_to_itself()
        {
            string image = Write(LegacyLauncher.CurrentName, "the launcher");

            Assert.Null(LegacyLauncher.ResolveHandOff(image));
        }

        [Fact]
        public void Some_other_launcher_is_left_alone()
        {
            Write(LegacyLauncher.CurrentName, "the launcher");
            string image = Write("SomeoneElsesLauncher.exe", "not ours");

            Assert.Null(LegacyLauncher.ResolveHandOff(image));
        }

        /// <summary>
        /// A developer runs ConquerLoader.exe out of bin\Release with no sibling
        /// beside it, and so does every player for the one session between the
        /// rename shipping and the patch pass installing the new file. Both carry
        /// on as this process.
        /// </summary>
        [Fact]
        public void Without_the_new_file_beside_it_the_old_name_is_still_the_launcher()
        {
            string image = Write(LegacyLauncher.LegacyName, "legacy copy");

            Assert.Null(LegacyLauncher.ResolveHandOff(image));
        }

        /// <summary>
        /// A zero-length EternalAbyss.exe is not something the installer can
        /// produce - it writes a part file and moves it - so this is about a file
        /// that arrived some other way. Handing off to it starts nothing and
        /// leaves the player with no launcher at all.
        /// </summary>
        [Fact]
        public void An_empty_file_under_the_new_name_is_not_handed_off_to()
        {
            Write(LegacyLauncher.CurrentName, "");
            string image = Write(LegacyLauncher.LegacyName, "legacy copy");

            Assert.Null(LegacyLauncher.ResolveHandOff(image));
        }

        /// <summary>
        /// A directory named EternalAbyss.exe answers false to FileInfo.Exists,
        /// which is the answer that matters, and the check must not throw on the
        /// way to it.
        /// </summary>
        [Fact]
        public void A_directory_under_the_new_name_is_not_handed_off_to()
        {
            Directory.CreateDirectory(Path.Combine(_root, LegacyLauncher.CurrentName));
            string image = Write(LegacyLauncher.LegacyName, "legacy copy");

            Assert.Null(LegacyLauncher.ResolveHandOff(image));
        }

        [Fact]
        public void An_unknown_running_image_is_never_a_hand_off()
        {
            Write(LegacyLauncher.CurrentName, "the launcher");

            Assert.Null(LegacyLauncher.ResolveHandOff(null));
            Assert.Null(LegacyLauncher.ResolveHandOff(""));
        }

        /// <summary>
        /// The guard is a separate variable from the self-update's on purpose: a
        /// hand-off must not spend the one restart the self-update is allowed.
        /// </summary>
        [Fact]
        public void The_guard_is_not_the_self_updates_guard()
        {
            Assert.NotEqual(SelfUpdate.GuardVariable, LegacyLauncher.GuardVariable);
        }
    }
}
