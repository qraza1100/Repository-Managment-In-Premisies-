using System.Security.Cryptography;
using System.Text;

namespace QualityAviationCodeRepositoryManagmentApi.API.Services
{
    public interface IFileService
    {
        string ComputeFileHash(byte[] fileContent);
        string ComputeFileHash(string filePath);
        bool CompareHashes(string hash1, string hash2);
        void EnsureDirectoryExists(string path);
        void WriteFile(string filePath, byte[] content);
        byte[] ReadFile(string filePath);
        bool FileExists(string filePath);
        void DeleteFile(string filePath);
        void DeleteDirectory(string path);
        List<string> GetFilesInDirectory(string path);
        long GetFileSize(byte[] content);
        long GetDirectorySize(string path);
    }

    public class FileService : IFileService
    {
        public string ComputeFileHash(byte[] fileContent)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedBytes = sha256.ComputeHash(fileContent);
                return Convert.ToBase64String(hashedBytes);
            }
        }

        public string ComputeFileHash(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hashedBytes = sha256.ComputeHash(stream);
                return Convert.ToBase64String(hashedBytes);
            }
        }

        public bool CompareHashes(string hash1, string hash2)
        {
            return hash1.Equals(hash2, StringComparison.OrdinalIgnoreCase);
        }

        public void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public void WriteFile(string filePath, byte[] content)
        {
            var directory = Path.GetDirectoryName(filePath);
            EnsureDirectoryExists(directory);
            File.WriteAllBytes(filePath, content);
        }

        public byte[] ReadFile(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            return File.ReadAllBytes(filePath);
        }

        public bool FileExists(string filePath)
        {
            return File.Exists(filePath);
        }

        public void DeleteFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        public List<string> GetFilesInDirectory(string path)
        {
            var files = new List<string>();
            if (!Directory.Exists(path))
                return files;

            try
            {
                var fileEntries = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                files.AddRange(fileEntries);
            }
            catch { }

            return files;
        }

        public long GetFileSize(byte[] content)
        {
            return content?.Length ?? 0;
        }

        public long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path))
                return 0;

            long size = 0;
            try
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    size += new FileInfo(file).Length;
                }
            }
            catch { }

            return size;
        }
    }
}
