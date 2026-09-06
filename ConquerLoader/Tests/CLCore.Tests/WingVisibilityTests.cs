using System;
using System.IO;
using System.Text;
using CLCore.ClientOptions;
using Xunit;

namespace CLCore.Tests.ClientOptions
{
    /// <summary>
    /// The wing toggle.
    ///
    /// WHAT IS ACTUALLY UNDER TEST is the pair of properties the feature rests
    /// on and nothing else:
    ///
    ///   * It changes the wing bindings and only the wing bindings. This file is
    ///     11,241 lines of effect bindings for the whole game; touching a line
    ///     that is not a wing would break art nobody was asking about.
    ///   * Hide and show are exact inverses, and each is idempotent. There is no
    ///     backup file and no saved list of stock names, so the file being its
    ///     own record of what to restore is the whole design.
    ///
    /// The line endings get their own tests because the real file is mixed - LF
    /// throughout with a tail of eighteen CRLF lines - and the patcher compares
    /// this file by hash, so a rewrite that normalises them turns every launch
    /// into a re-download.
    /// </summary>
    public sealed class WingVisibilityTests : IDisposable
    {
        private readonly string _root;

        public WingVisibilityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "clwings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static byte[] Bytes(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        private static string Text(byte[] bytes)
        {
            return Encoding.ASCII.GetString(bytes);
        }

        private static byte[] Hide(byte[] source, out int changed)
        {
            return WingVisibility.Rewrite(source, true, out changed);
        }

        private static byte[] Show(byte[] source, out int changed)
        {
            return WingVisibility.Rewrite(source, false, out changed);
        }

        /// <summary>A few real lines, including the two states and a non-wing neighbour.</summary>
        private const string Sample =
            "999.9999.204.009=_p_24_wing1_close110\n" +
            "1999.9999.204.009=_p_24_wing1_open110\n" +
            "999.0100.130.300=f-taoist\n" +
            "999.9999.219.129=_p_24_wing16_close\n" +
            "0001.9999.195.470=_p_31_7198100v_head\n";

        [Fact]
        public void Hide_marks_every_wing_binding()
        {
            int changed;
            string result = Text(Hide(Bytes(Sample), out changed));

            Assert.Equal(3, changed);
            Assert.Contains("999.9999.204.009=_p_24_wing1_close110_hidden\n", result);
            Assert.Contains("1999.9999.204.009=_p_24_wing1_open110_hidden\n", result);
            Assert.Contains("999.9999.219.129=_p_24_wing16_close_hidden\n", result);
        }

        [Fact]
        public void Hide_leaves_every_other_binding_exactly_as_it_was()
        {
            int changed;
            string result = Text(Hide(Bytes(Sample), out changed));

            Assert.Contains("999.0100.130.300=f-taoist\n", result);
            Assert.Contains("0001.9999.195.470=_p_31_7198100v_head\n", result);
            Assert.DoesNotContain("f-taoist_hidden", result);
            Assert.DoesNotContain("v_head_hidden", result);
        }

        [Fact]
        public void Show_undoes_hide_byte_for_byte()
        {
            byte[] original = Bytes(Sample);

            int hidden, shown;
            byte[] restored = Show(Hide(original, out hidden), out shown);

            Assert.Equal(3, hidden);
            Assert.Equal(3, shown);
            Assert.Equal(original, restored);
        }

        [Fact]
        public void Hiding_twice_changes_nothing_the_second_time()
        {
            int first, second;
            byte[] once = Hide(Bytes(Sample), out first);
            byte[] twice = Hide(once, out second);

            Assert.Equal(3, first);
            Assert.Equal(0, second);
            Assert.Equal(once, twice);
        }

        [Fact]
        public void Showing_an_untouched_file_changes_nothing()
        {
            byte[] original = Bytes(Sample);

            int changed;
            byte[] result = Show(original, out changed);

            Assert.Equal(0, changed);
            Assert.Equal(original, result);
        }

        [Fact]
        public void Mixed_line_endings_survive_a_round_trip()
        {
            // The shape of the real file: LF throughout, CRLF at the tail.
            byte[] original = Bytes(
                "999.9999.204.009=_p_24_wing1_close110\n" +
                "999.0100.130.300=f-taoist\n" +
                "999.9999.205.019=_p_24_wing3_close110\r\n" +
                "0001.9999.195.470=_p_31_7198100v_head\r\n");

            int hidden, shown;
            byte[] hiddenBytes = Hide(original, out hidden);

            Assert.Equal(2, hidden);
            Assert.Contains("_p_24_wing1_close110_hidden\n999", Text(hiddenBytes));
            Assert.Contains("_p_24_wing3_close110_hidden\r\n", Text(hiddenBytes));

            Assert.Equal(original, Show(hiddenBytes, out shown));
        }

        [Fact]
        public void A_file_that_does_not_end_in_a_newline_still_does_not()
        {
            byte[] original = Bytes("999.9999.204.009=_p_24_wing1_close110");

            int changed;
            byte[] result = Hide(original, out changed);

            Assert.Equal(1, changed);
            Assert.Equal("999.9999.204.009=_p_24_wing1_close110_hidden", Text(result));
        }

        [Fact]
        public void Blank_lines_and_lines_without_a_value_are_copied_through()
        {
            byte[] original = Bytes("\n[Section]\n\n999.9999.204.009=_p_24_wing1_close110\n\n");

            int hidden, shown;
            byte[] hiddenBytes = Hide(original, out hidden);

            Assert.Equal(1, hidden);
            Assert.Contains("\n[Section]\n\n", Text(hiddenBytes));
            Assert.Equal(original, Show(hiddenBytes, out shown));
        }

        [Fact]
        public void An_empty_file_is_left_empty()
        {
            int changed;
            Assert.Empty(WingVisibility.Rewrite(new byte[0], true, out changed));
            Assert.Equal(0, changed);
        }

        [Fact]
        public void A_missing_file_is_reported_rather_than_thrown()
        {
            WingRewriteResult result = WingVisibility.Apply(_root, true, null);

            Assert.Equal(WingRewriteStatus.FileMissing, result.Status);
        }

        [Fact]
        public void Apply_writes_the_file_and_can_put_it_back()
        {
            string path = Path.Combine(_root, WingVisibility.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Bytes(Sample));

            WingRewriteResult hidden = WingVisibility.Apply(_root, true, null);
            Assert.Equal(WingRewriteStatus.Rewritten, hidden.Status);
            Assert.Equal(3, hidden.LinesChanged);
            Assert.Contains("_p_24_wing1_close110_hidden", File.ReadAllText(path));

            WingRewriteResult shown = WingVisibility.Apply(_root, false, null);
            Assert.Equal(WingRewriteStatus.Rewritten, shown.Status);
            Assert.Equal(Sample, File.ReadAllText(path).Replace("\r\n", "\n"));
        }

        [Fact]
        public void Apply_does_not_touch_a_file_that_already_agrees()
        {
            string path = Path.Combine(_root, WingVisibility.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Bytes(Sample));

            DateTime before = File.GetLastWriteTimeUtc(path);

            WingRewriteResult result = WingVisibility.Apply(_root, false, null);

            Assert.Equal(WingRewriteStatus.AlreadyCorrect, result.Status);
            Assert.Equal(0, result.LinesChanged);
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }

        [Fact]
        public void Apply_leaves_no_partial_file_behind()
        {
            string path = Path.Combine(_root, WingVisibility.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Bytes(Sample));

            WingVisibility.Apply(_root, true, null);

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path), "*-part"));
        }
    }
}
