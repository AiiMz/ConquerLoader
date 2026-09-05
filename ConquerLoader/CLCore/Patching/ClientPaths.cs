using System;
using System.IO;

namespace CLCore.Patching
{
    /// <summary>
    /// Turns a path that came off the network into a path this process is
    /// willing to write.
    ///
    /// THIS IS THE SECURITY BOUNDARY OF THE WHOLE PATCHER. Everything else here
    /// reads a file or fetches a URL; this is the only place where something a
    /// server said becomes a location on a player's disk. The manifest is served
    /// over TLS from a host the server operator runs, which is a reason to expect
    /// the paths to be fine and not a reason to skip checking them - a
    /// compromised or misbuilt manifest with "../../Windows/System32/..." in it
    /// would otherwise be executed faithfully by a program running on a player's
    /// machine with that player's rights.
    ///
    /// The rules are deliberately stricter than "does not escape the root". A
    /// path this tool cannot describe in one sentence is a path it refuses.
    /// </summary>
    public static class ClientPaths
    {
        /// <summary>
        /// True if <paramref name="path"/> is a manifest path this tool will
        /// write. On false, <paramref name="problem"/> says which rule failed, in
        /// words that can go straight to a player.
        /// </summary>
        public static bool IsSafeRelativePath(string path, out string problem)
        {
            problem = null;

            if (string.IsNullOrEmpty(path))
            {
                problem = "the path is empty";
                return false;
            }

            // FIRST, because Path.IsPathRooted below is not safe to call on a
            // string containing one. On .NET Framework that method validates its
            // argument and throws ArgumentException for illegal path characters;
            // on .NET Core the validation was removed and it simply answers. This
            // check ran second when the code lived in a .NET 8 executable and the
            // difference only showed up as a failing test after the port.
            foreach (char c in path)
            {
                if (c < ' ' || c == (char)127)
                {
                    problem = "the path contains a control character";
                    return false;
                }
            }

            // A rooted path, a UNC path, or anything with a drive letter. The
            // colon test also covers NTFS alternate data streams (a:b), which
            // Path.IsPathRooted does not.
            if (Path.IsPathRooted(path) || path.IndexOf(':') >= 0)
            {
                problem = "the path is absolute or names a drive";
                return false;
            }

            // Manifest paths use forward slashes, always. A backslash here is
            // either a different tool's idea of a path or an attempt to smuggle
            // a separator past a check that only looked at '/'.
            if (path.IndexOf('\\') >= 0)
            {
                problem = "the path contains a backslash";
                return false;
            }

            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0)
                {
                    problem = "the path has an empty segment";
                    return false;
                }

                if (segment == "." || segment == "..")
                {
                    problem = "the path contains a relative segment";
                    return false;
                }

                // Trailing dots and spaces are stripped by Win32 when a file is
                // opened, so "ini\\foo.dat " and "ini\\foo.dat" are the same file
                // to Windows and different strings to us. That is a way for two
                // manifest entries to fight over one file, and it is never
                // something the generator produces.
                if (segment[segment.Length - 1] == ' ' || segment[segment.Length - 1] == '.')
                {
                    problem = "a path segment ends with a space or a dot";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Resolves a validated manifest path against the client root, and
        /// re-checks containment against the resolved absolute path.
        ///
        /// The second check is belt and braces on purpose: the rules above are
        /// string rules, and this one asks the runtime where the file actually
        /// lands. A symlink or a junction in the client tree is the case string
        /// rules cannot see.
        /// </summary>
        public static string Resolve(string clientRoot, string relativePath)
        {
            string problem;
            if (!IsSafeRelativePath(relativePath, out problem))
                throw new InvalidDataException("Refusing the manifest path '" + relativePath + "': " + problem + ".");

            string rootFull = Path.GetFullPath(clientRoot);
            string combined = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refusing the manifest path '" + relativePath + "': it resolves outside the client directory.");

            return combined;
        }

        /// <summary>
        /// The URL a manifest entry is fetched from: the base URL, then "files/",
        /// then the path with each segment escaped.
        ///
        /// Escaped per SEGMENT rather than whole, because Uri.EscapeDataString
        /// escapes '/' and this path needs to keep its directory structure. A
        /// Conquer client tree has spaces and parentheses in filenames, which are
        /// the characters this exists for.
        /// </summary>
        public static Uri FileUrl(Uri baseUrl, string relativePath)
        {
            string[] segments = relativePath.Split('/');
            for (int i = 0; i < segments.Length; i++)
                segments[i] = Uri.EscapeDataString(segments[i]);

            return new Uri(baseUrl, "files/" + string.Join("/", segments));
        }
    }
}
