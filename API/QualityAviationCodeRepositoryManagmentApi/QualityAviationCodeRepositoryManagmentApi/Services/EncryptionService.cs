using System.Security.Cryptography;
using System.Text;

namespace QualityAviationCodeRepositoryManagmentApi.API.Services
{
    public class EncryptionService
    {
        private readonly string _encryptionKey;
        private readonly string _encryptionIV;

        public EncryptionService(IConfiguration configuration)
        {
            _encryptionKey = configuration["EncryptionSettings:Key"] ?? "YourSecretKeyHere32CharacterKeyX";
            _encryptionIV = configuration["EncryptionSettings:IV"] ?? "YourIVHere16Char";
        }

        public string Encrypt(string plainText)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(_encryptionKey);
                aes.IV = Encoding.UTF8.GetBytes(_encryptionIV);

                var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    {
                        using (var sw = new StreamWriter(cs))
                        {
                            sw.Write(plainText);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }

        public string Decrypt(string cipherText)
        {
            try
            {
                using (var aes = Aes.Create())
                {
                    aes.Key = Encoding.UTF8.GetBytes(_encryptionKey);
                    aes.IV = Encoding.UTF8.GetBytes(_encryptionIV);

                    var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                    using (var ms = new MemoryStream(Convert.FromBase64String(cipherText)))
                    {
                        using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        {
                            using (var sr = new StreamReader(cs))
                            {
                                return sr.ReadToEnd();
                            }
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public string GenerateHash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hashedBytes);
            }
        }
    }
}
