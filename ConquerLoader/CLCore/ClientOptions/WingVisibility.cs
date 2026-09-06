using System;
using System.IO;
using System.Text;

namespace CLCore.ClientOptions
{
    public enum WingRewriteStatus
    {
        /// <summary>The file already said what the setting asks for. Nothing was written.</summary>
        AlreadyCorrect,
        Rewritten,
        FileMissing,
        Failed,
    }

    public sealed class WingRewriteResult
    {
        public WingRewriteStatus Status;
        public int LinesChanged;
        public string Message;
    }

    /// <summary>
    /// Turns the drawing of wings on and off in the client, without touching the
    /// item that grants them.
    ///
    /// WHY THIS IS A CLIENT EDIT AND NOT A SERVER ONE
    /// ========================================================================
    /// Wings are worn at equipment position 19 and are worth real battle power,
    /// attack and defence. Every server-side attempt to hide them has had to lie
    /// to the client about what is equipped - blank the slot, or swap the item id
    /// for a look-alike - and the client works its own battle power out from that
    /// same table, so the number it shows the player drops even though nothing on
    /// the server moved. There is no version of that lie which is only cosmetic.
    ///
    /// Here nothing is lied about. The server sends the real item, the client
    /// counts it, and the one thing that changes is whether the client can find
    /// the art to draw.
    ///
    /// HOW THE CLIENT RESOLVES A PAIR OF WINGS
    /// ========================================================================
    /// ini\Action3DEffect.ini binds an effect to an action, one per line:
    ///
    ///     999.9999.204.009=_p_24_wing1_close110      wings folded
    ///     1999.9999.204.009=_p_24_wing1_open110      wings spread
    ///
    /// The key is state.9999.(item id high three).(item id low three), where the
    /// low three carry the refinement level in steps of ten - .009 through .129
    /// is +0 through +12 of the same pair of wings. The value names a folder
    /// under c3\effect\wing.
    ///
    /// A key the client looks up and does not find draws nothing, and that is
    /// already the client normal behaviour rather than an error path: wings below
    /// Super quality have no binding at all, which is what the item means when it
    /// says you must upgrade it before you will grow a pair.
    ///
    /// WHY A SUFFIX RATHER THAN A REPLACEMENT
    /// ========================================================================
    /// Hiding appends <see cref="Marker"/> to the effect name, giving a folder
    /// that does not exist; showing takes it off again. The original name stays
    /// in the line, so this is reversible from the file alone - there is no
    /// backup copy to keep in step, and no list of the stock names to go stale
    /// when art is added. Both directions are idempotent, so running either one
    /// twice is harmless, and a line hand-edited to something unrecognisable is
    /// left exactly as it is rather than guessed at.
    ///
    /// IT MUST RUN AFTER THE PATCHER. The patch step compares by hash, so it
    /// restores this file the moment it sees the suffix. That is not a conflict
    /// to design around: the patcher hands over the current stock file and this
    /// re-applies the player choice to it, every launch.
    /// </summary>
    public static class WingVisibility
    {
        /// <summary>Relative to the client root, which is the loader own folder.</summary>
        public const string RelativePath = @"ini\Action3DEffect.ini";

        private const string PartialSuffix = ".wingtoggle-part";

        /// <summary>
        /// Every wing effect in the file is named for the body part it hangs off
        /// (24) and nothing else is, so this prefix selects the wing bindings and
        /// only those - 414 of the file 11,241 lines.
        /// </summary>
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("_p_24_wing");

        /// <summary>
        /// Appended to make the name miss. Plain ASCII, and it keeps the longest
        /// wing name at 27 characters, one under the longest name the file
        /// already carries - so nothing here is the first of its size.
        /// </summary>
        private static readonly byte[] Marker = Encoding.ASCII.GetBytes("_hidden");

        /// <summary>
        /// Brings <see cref="RelativePath"/> under <paramref name="clientRoot"/>
        /// into line with <paramref name="hide"/>.
        ///
        /// Never throws and never asks for the launch to be stopped. Getting this
        /// wrong shows the player wings they did not want, or hides wings they
        /// did; it cannot put the client out of step with the server, because the
        /// server neither reads this file nor knows the setting exists.
        /// </summary>
        public static WingRewriteResult Apply(string clientRoot, bool hide, Action<string> log)
        {
            WingRewriteResult result = new WingRewriteResult();

            try
            {
                string path = Path.Combine(clientRoot ?? string.Empty, RelativePath);

                if (!File.Exists(path))
                {
                    result.Status = WingRewriteStatus.FileMissing;
                    result.Message = RelativePath + " is not there, so there is nothing to hide or show.";
                    Log(log, result.Message);
                    return result;
                }

                byte[] before = File.ReadAllBytes(path);
                int changed;
                byte[] after = Rewrite(before, hide, out changed);

                result.LinesChanged = changed;

                if (changed == 0)
                {
                    // Not merely an optimisation. With the setting off and the file
                    // stock, writing it anyway would change its timestamp for no
                    // reason on every single launch.
                    result.Status = WingRewriteStatus.AlreadyCorrect;
                    result.Message = "Wings are already " + (hide ? "hidden" : "shown") + "; left " + RelativePath + " alone.";
                    Log(log, result.Message);
                    return result;
                }

                WriteAtomically(path, after);

                result.Status = WingRewriteStatus.Rewritten;
                result.Message = (hide ? "Hid " : "Restored ") + changed + " wing effect binding" +
                    (changed == 1 ? "" : "s") + " in " + RelativePath + ".";
                Log(log, result.Message);
                return result;
            }
            catch (Exception ex)
            {
                // Cosmetic either way, so this is reported and stepped over. The
                // write is atomic, so a failure here cannot have left the client a
                // half-written ini to parse.
                result.Status = WingRewriteStatus.Failed;
                result.Message = "Could not " + (hide ? "hide" : "restore") + " wings: " + ex.Message;
                Log(log, result.Message);
                return result;
            }
        }

        /// <summary>
        /// The transform itself, on bytes.
        ///
        /// BYTES, NOT LINES OF TEXT. Action3DEffect.ini is 11,241 lines of LF with
        /// a tail of eighteen that end CRLF. Reading it as text and writing it back
        /// rewrites those eighteen line endings as a side effect, which is a diff
        /// nobody asked for in a file the patcher compares by hash. Copying the
        /// bytes through and editing only inside the values means every byte this
        /// does not mean to change is the byte that was already there.
        /// </summary>
        public static byte[] Rewrite(byte[] source, bool hide, out int linesChanged)
        {
            linesChanged = 0;
            if (source == null) return new byte[0];

            using (MemoryStream output = new MemoryStream(source.Length + 4096))
            {
                int i = 0;
                while (i < source.Length)
                {
                    int lineEnd = i;
                    while (lineEnd < source.Length && source[lineEnd] != (byte)'\n') lineEnd++;

                    // The line content, with any CR held back so it can go out
                    // again untouched behind whatever is written.
                    int contentEnd = lineEnd;
                    if (contentEnd > i && source[contentEnd - 1] == (byte)'\r') contentEnd--;

                    bool wrote = false;
                    int equals = IndexOf(source, i, contentEnd, (byte)'=');

                    if (equals >= 0)
                    {
                        int valueStart = equals + 1;
                        int valueLength = contentEnd - valueStart;

                        if (StartsWith(source, valueStart, valueLength, Prefix))
                        {
                            bool marked = EndsWith(source, valueStart, valueLength, Marker);

                            if (hide && !marked)
                            {
                                output.Write(source, i, contentEnd - i);
                                output.Write(Marker, 0, Marker.Length);
                                linesChanged++;
                                wrote = true;
                            }
                            else if (!hide && marked)
                            {
                                output.Write(source, i, contentEnd - i - Marker.Length);
                                linesChanged++;
                                wrote = true;
                            }
                        }
                    }

                    if (!wrote) output.Write(source, i, contentEnd - i);

                    // The CR if this line had one, then the LF - unless the file
                    // simply ended, in which case it ended without one and still
                    // does.
                    output.Write(source, contentEnd, lineEnd - contentEnd);
                    if (lineEnd < source.Length) output.WriteByte((byte)'\n');

                    i = lineEnd + 1;
                }

                return output.ToArray();
            }
        }

        private static void WriteAtomically(string destination, byte[] content)
        {
            string partial = destination + PartialSuffix;

            try
            {
                File.WriteAllBytes(partial, content);

                // File.Move has no overwrite overload on .NET Framework, so the
                // destination goes first. The window in which the file is absent is
                // safe here for the same reason it is in the patcher: this runs
                // before the client is started.
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(partial, destination);
            }
            finally
            {
                if (File.Exists(partial))
                {
                    try { File.Delete(partial); } catch (IOException) { }
                }
            }
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null) log(message);
        }

        private static int IndexOf(byte[] buffer, int start, int end, byte value)
        {
            for (int i = start; i < end; i++)
                if (buffer[i] == value) return i;
            return -1;
        }

        private static bool StartsWith(byte[] buffer, int start, int length, byte[] value)
        {
            if (length < value.Length) return false;
            for (int i = 0; i < value.Length; i++)
                if (buffer[start + i] != value[i]) return false;
            return true;
        }

        private static bool EndsWith(byte[] buffer, int start, int length, byte[] value)
        {
            if (length < value.Length) return false;
            int offset = start + length - value.Length;
            for (int i = 0; i < value.Length; i++)
                if (buffer[offset + i] != value[i]) return false;
            return true;
        }
    }
}
