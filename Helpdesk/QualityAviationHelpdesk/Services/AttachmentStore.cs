using System.Security.Cryptography;

namespace QualityAviationHelpdesk.Services
{
    // Screenshots and documents attached to tickets. Kept on disk in the application's data folder (it outlives
    // releases), never under wwwroot, under a random name; the database records what each one is.
    //
    // A file is accepted only when its contents match its extension, and only from a short list of types that cannot
    // run in a browser or on a PC by being opened: no HTML, SVG, scripts or programs.
    public class AttachmentStore
    {
        public const int MaxFilesPerUpload = 5;

        private sealed record Kind(string ContentType, Func<byte[], int, bool> Matches, bool Image, bool IsText = false);

        private static readonly Dictionary<string, Kind> Kinds = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = new("image/png", (b, n) => Starts(b, n, 0x89, 0x50, 0x4E, 0x47), true),
            [".jpg"] = new("image/jpeg", (b, n) => Starts(b, n, 0xFF, 0xD8, 0xFF), true),
            [".jpeg"] = new("image/jpeg", (b, n) => Starts(b, n, 0xFF, 0xD8, 0xFF), true),
            [".gif"] = new("image/gif", (b, n) => Starts(b, n, 0x47, 0x49, 0x46, 0x38), true),
            [".webp"] = new("image/webp", (b, n) => n >= 12 && Starts(b, n, 0x52, 0x49, 0x46, 0x46) && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50, true),
            [".bmp"] = new("image/bmp", (b, n) => Starts(b, n, 0x42, 0x4D), true),
            [".pdf"] = new("application/pdf", (b, n) => Starts(b, n, 0x25, 0x50, 0x44, 0x46), false),
            [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", Zip, false),
            [".xlsx"] = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Zip, false),
            [".pptx"] = new("application/vnd.openxmlformats-officedocument.presentationml.presentation", Zip, false),
            [".zip"] = new("application/zip", Zip, false),
            [".doc"] = new("application/msword", Ole, false),
            [".xls"] = new("application/vnd.ms-excel", Ole, false),
            [".msg"] = new("application/vnd.ms-outlook", Ole, false),
            [".txt"] = new("text/plain", Text, false, true),
            [".log"] = new("text/plain", Text, false, true),
            [".csv"] = new("text/csv", Text, false, true),
            [".eml"] = new("message/rfc822", Text, false, true)
        };

        public static string AcceptList => string.Join(",", Kinds.Keys);

        private readonly string _root;

        public AttachmentStore(string dataDirectory)
        {
            _root = Path.Combine(dataDirectory, "attachments");
            Directory.CreateDirectory(Path.Combine(_root, "incoming"));
        }

        public sealed record Stored(Guid Id, string FileName, string ContentType, long SizeBytes, string Sha256, string StoredPath);

        // Checks and stores one upload. Throws ValidationException with a sentence the uploader can act on.
        public async Task<Stored> SaveAsync(string originalName, Stream content, long maxBytes, CancellationToken cancellationToken)
        {
            var fileName = CleanName(originalName);
            var extension = Path.GetExtension(fileName);
            if (!Kinds.TryGetValue(extension, out var kind))
                throw new ValidationException(new[] { $"\"{fileName}\" cannot be attached: allowed are screenshots (png, jpg, gif, webp, bmp), PDF, Office files, zip, txt, log, csv and e-mails (eml, msg)." });

            var id = Guid.NewGuid();
            var incoming = Path.Combine(_root, "incoming", id.ToString("N"));
            long size = 0;
            var head = new byte[16];
            var headLength = 0;
            var text = kind.IsText;

            try
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var target = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        size += read;
                        if (size > maxBytes)
                            throw new ValidationException(new[] { $"\"{fileName}\" is larger than {maxBytes / 1024 / 1024} MB." });

                        if (headLength < head.Length)
                        {
                            var take = Math.Min(head.Length - headLength, read);
                            Array.Copy(buffer, 0, head, headLength, take);
                            headLength += take;
                        }

                        // A text file with NUL bytes in it is something else wearing a .txt name
                        if (text && size - read < 65536 && Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                            throw new ValidationException(new[] { $"\"{fileName}\" is not a plain text file." });

                        sha.AppendData(buffer, 0, read);
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                }

                if (size == 0) throw new ValidationException(new[] { $"\"{fileName}\" is empty." });
                if (!kind.Matches(head, headLength))
                    throw new ValidationException(new[] { $"\"{fileName}\" does not contain what its name says ({extension})." });

                var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), id.ToString("N") + ".bin");
                var final = Path.Combine(_root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(incoming, final);

                return new Stored(id, fileName, kind.ContentType, size, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), relative);
            }
            finally
            {
                if (File.Exists(incoming)) File.Delete(incoming);
            }
        }

        public FileStream Open(string storedPath)
        {
            var full = Path.GetFullPath(Path.Combine(_root, storedPath));
            if (!full.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new NotFoundException("The file is not available.");
            if (!File.Exists(full)) throw new NotFoundException("The file is no longer on the server.");
            return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }

        // Images may be shown in the page; everything else is always downloaded
        public static bool IsImage(string contentType) => Kinds.Values.Any(k => k.Image && k.ContentType == contentType);

        // Removes files stored for an upload whose ticket or comment was not saved after all
        public void Discard(IEnumerable<Stored> files)
        {
            foreach (var file in files)
            {
                try
                {
                    File.Delete(Path.Combine(_root, file.StoredPath));
                }
                catch (IOException) { }
            }
        }

        private static string CleanName(string name)
        {
            name = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last()).Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            name = new string(name.Where(c => !char.IsControl(c)).ToArray());
            if (name.Length == 0) name = "file";
            if (name.Length > 150) name = name[..100] + "…" + name[^40..];
            return name;
        }

        private static bool Starts(byte[] bytes, int length, params byte[] magic)
        {
            if (length < magic.Length) return false;
            for (var i = 0; i < magic.Length; i++)
                if (bytes[i] != magic[i]) return false;
            return true;
        }

        private static bool Zip(byte[] b, int n) => Starts(b, n, 0x50, 0x4B, 0x03, 0x04);
        private static bool Ole(byte[] b, int n) => Starts(b, n, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);
        private static bool Text(byte[] b, int n) => true;
    }
}
