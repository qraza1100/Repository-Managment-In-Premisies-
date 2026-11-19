using System.Security.Cryptography;
using System.Text;

namespace QualityAviationRepositoryShared
{
    // One file of a tree: where it is, how big it is, and the SHA-256 of its content.
    public class ManifestEntry
    {
        public string Path { get; set; }     // relative, '/' separated
        public long Size { get; set; }
        public string Hash { get; set; }     // SHA-256 in Base64, the format the repository API records for every pushed file
    }

    public class FileChange
    {
        public const string Added = "added";
        public const string Modified = "modified";
        public const string Deleted = "deleted";

        public string Path { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
    }

    // A folder tree described by hashes. Two trees are identical exactly when their manifests are, which is what
    // lets a push, a pull and a deployment each work out what changed without sending or comparing file contents.
    public class FileManifest
    {
        public string Version { get; set; }
        public List<ManifestEntry> Files { get; set; } = new();

        public Dictionary<string, ManifestEntry> ByPath()
        {
            var byPath = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files) byPath[file.Path] = file;
            return byPath;
        }
    }

    public static class Manifests
    {
        public static string HashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            return Convert.ToBase64String(SHA256.HashData(stream));
        }

        public static FileManifest FromFiles(IEnumerable<(string FullPath, string RelativePath)> files)
        {
            var manifest = new FileManifest();
            foreach (var (fullPath, relativePath) in files)
            {
                manifest.Files.Add(new ManifestEntry
                {
                    Path = relativePath.Replace('\\', '/'),
                    Size = new FileInfo(fullPath).Length,
                    Hash = HashFile(fullPath)
                });
            }
            return manifest;
        }

        public static FileManifest FromDirectory(string root, Func<string, bool> skip = null)
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => (FullPath: file, RelativePath: System.IO.Path.GetRelativePath(root, file).Replace('\\', '/')))
                .Where(file => skip == null || !skip(file.RelativePath));
            return FromFiles(files);
        }

        // What has to happen to a tree that looks like 'from' to make it look like 'to'
        public static List<FileChange> Diff(FileManifest from, FileManifest to)
        {
            var before = from?.ByPath() ?? new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            var after = to?.ByPath() ?? new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<FileChange>();

            foreach (var file in after.Values)
            {
                if (!before.TryGetValue(file.Path, out var old))
                    changes.Add(new FileChange { Path = file.Path, Kind = FileChange.Added, Size = file.Size });
                else if (old.Hash != file.Hash)
                    changes.Add(new FileChange { Path = file.Path, Kind = FileChange.Modified, Size = file.Size });
            }

            foreach (var file in before.Values)
                if (!after.ContainsKey(file.Path))
                    changes.Add(new FileChange { Path = file.Path, Kind = FileChange.Deleted, Size = file.Size });

            return changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // One value that stands for the whole tree: equal fingerprints mean the same files with the same content
        public static string Fingerprint(FileManifest manifest)
        {
            var text = new StringBuilder();
            foreach (var file in manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
                text.Append(file.Path).Append('\n').Append(file.Hash).Append('\n');

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    // What travels with a push that only carries the changed files. The receiver rebuilds the whole tree from the
    // base version plus the files in the archive, and refuses the result unless it has exactly this fingerprint.
    public class DeltaDescription
    {
        public const string EntryName = ".qadelta.json";

        public string BaseVersion { get; set; }
        public List<string> Deleted { get; set; } = new();
        public string Fingerprint { get; set; }
    }
}
