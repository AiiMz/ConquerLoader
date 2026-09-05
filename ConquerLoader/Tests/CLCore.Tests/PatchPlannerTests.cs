using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CLCore.Patching;
using Xunit;

namespace CLCore.Tests.Patching
{
    /// <summary>
    /// The compare step. Ported from the EternalAbyss standalone patcher when the patch step moved inside the loader.
    ///
    /// A real tree in a temp directory, so nothing here needs a real Conquer
    /// client - which a fresh clone and a build agent both lack.
    /// </summary>
    public sealed class PatchPlannerTests : IDisposable
    {
        private readonly string _root;

        public PatchPlannerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "clpatch-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private ManifestFile Write(string relativePath, string content)
        {
            string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));

            return new ManifestFile
            {
                Path = relativePath,
                Size = new FileInfo(full).Length,
                Sha256 = PatchPlanner.HashFile(full),
            };
        }

        private static Manifest Of(params ManifestFile[] files)
        {
            return new Manifest
            {
                SchemaVersion = 1,
                Version = "test",
                FileCount = files.Length,
                TotalBytes = files.Sum(f => f.Size),
                Files = new List<ManifestFile>(files),
            };
        }

        [Fact]
        public void An_untouched_install_needs_nothing()
        {
            PatchPlan plan = PatchPlanner.Create(_root, Of(Write("ini/a.dat", "one"), Write("c3/b.c3", "two")));

            Assert.True(plan.IsUpToDate);
            Assert.Equal(2, plan.UpToDateCount);
            Assert.Empty(plan.Downloads);
            Assert.Equal(0, plan.BytesToDownload);
        }

        [Fact]
        public void A_file_that_is_not_there_is_missing()
        {
            ManifestFile entry = Write("ini/a.dat", "one");
            File.Delete(Path.Combine(_root, "ini", "a.dat"));

            PatchPlan plan = PatchPlanner.Create(_root, Of(entry));

            Assert.Equal(1, plan.MissingCount);
            Assert.Equal(PatchReason.Missing, Assert.Single(plan.Downloads).Reason);
        }

        [Fact]
        public void A_truncated_file_is_caught_by_size_before_it_is_hashed()
        {
            ManifestFile entry = Write("ini/a.dat", "the whole thing");
            File.WriteAllText(Path.Combine(_root, "ini", "a.dat"), "the wh");

            PatchPlan plan = PatchPlanner.Create(_root, Of(entry));

            Assert.Equal(1, plan.WrongSizeCount);
            Assert.Equal(0, plan.WrongContentCount);
        }

        [Fact]
        public void An_edited_file_of_the_same_length_is_caught_by_the_hash()
        {
            // The case a version number cannot see, and the reason this patcher
            // hashes: same size, same name, different bytes.
            ManifestFile entry = Write("ini/a.dat", "AAAA");
            File.WriteAllText(Path.Combine(_root, "ini", "a.dat"), "BBBB");

            PatchPlan plan = PatchPlanner.Create(_root, Of(entry));

            Assert.Equal(1, plan.WrongContentCount);
            Assert.Equal(0, plan.WrongSizeCount);
            Assert.Equal(PatchReason.WrongContent, Assert.Single(plan.Downloads).Reason);
        }

        [Fact]
        public void Only_the_broken_file_is_downloaded()
        {
            // Corrupt one file in a working install and
            // the patcher should touch exactly that one.
            ManifestFile good = Write("ini/good.dat", "fine");
            ManifestFile bad = Write("ini/bad.dat", "AAAA");
            File.WriteAllText(Path.Combine(_root, "ini", "bad.dat"), "BBBB");

            PatchPlan plan = PatchPlanner.Create(_root, Of(good, bad));

            Assert.Equal("ini/bad.dat", Assert.Single(plan.Downloads).Entry.Path);
            Assert.Equal(bad.Size, plan.BytesToDownload);
        }

        [Fact]
        public void Bytes_to_download_counts_only_what_is_downloaded()
        {
            ManifestFile present = Write("ini/a.dat", "1234567890");
            ManifestFile absent = Write("ini/b.dat", "12345");
            File.Delete(Path.Combine(_root, "ini", "b.dat"));

            PatchPlan plan = PatchPlanner.Create(_root, Of(present, absent));

            Assert.Equal(absent.Size, plan.BytesToDownload);
        }

        [Fact]
        public void A_manifest_path_that_escapes_the_root_is_refused_rather_than_planned()
        {
            var manifest = Of(new ManifestFile
            {
                Path = "../escaped.dat",
                Size = 1,
                Sha256 = new string('a', 64),
            });

            Assert.Throws<InvalidDataException>(() => PatchPlanner.Create(_root, manifest));
        }
    }
}
