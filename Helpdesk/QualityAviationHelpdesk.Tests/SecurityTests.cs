using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using QualityAviationHelpdesk.Data;
using QualityAviationHelpdesk.Services;
using System.Text;

namespace QualityAviationHelpdesk.Tests
{
    public class AuditChainTests
    {
        private static AuditEvent Sample() => new()
        {
            EntityType = "ticket", EntityId = "1001", Action = "resolve", ActorKind = "it", ActorId = "jawad", ActorName = "jawad",
            Detail = "In progress → Resolved", VisibleToRequester = true, Ip = "10.0.248.15", At = AuditChain.Now(), PrevHash = AuditChain.Genesis
        };

        [Fact]
        public void The_hash_is_stable_and_covers_every_field()
        {
            var original = Sample();
            var hash = AuditChain.Hash(original);
            Assert.Equal(64, hash.Length);
            Assert.Equal(hash, AuditChain.Hash(original));

            // Changing anything at all gives a different hash
            foreach (var change in new Action<AuditEvent>[]
                     {
                         e => e.Action = "reject", e => e.ActorName = "someone", e => e.Detail = "edited", e => e.At = e.At.AddMilliseconds(1),
                         e => e.VisibleToRequester = false, e => e.PrevHash = new string('1', 64), e => e.EntityId = "1002", e => e.Ip = null
                     })
            {
                var copy = Sample();
                copy.At = original.At;
                change(copy);
                Assert.NotEqual(hash, AuditChain.Hash(copy));
            }
        }

        [Fact]
        public void Times_are_kept_to_the_millisecond_the_database_stores()
        {
            var now = AuditChain.Now();
            Assert.Equal(0, now.Ticks % TimeSpan.TicksPerMillisecond);
            Assert.Equal(DateTimeKind.Utc, now.Kind);
        }
    }

    public class LegacyPasswordTests
    {
        // Q+ CMR (.NET 5, ASP.NET Identity) stores V3 hashes: PBKDF2-HMACSHA256, 10 000 iterations.
        // The portal must accept them as they are, without rewriting them.
        [Fact]
        public void Hashes_made_by_the_dotnet5_application_are_accepted()
        {
            var stored = Dotnet5Hash("Corr3ct-Horse");
            // The prefix every one of the 174 older hashes in the Q+ CMR database has
            Assert.StartsWith("AQAAAAEAACcQ", stored);

            var portal = new PasswordHasher<LegacyUser>();
            Assert.NotEqual(PasswordVerificationResult.Failed, portal.VerifyHashedPassword(new LegacyUser(), stored, "Corr3ct-Horse"));
            Assert.Equal(PasswordVerificationResult.Failed, portal.VerifyHashedPassword(new LegacyUser(), stored, "corr3ct-horse"));
        }

        [Fact]
        public void Hashes_made_by_newer_identity_versions_are_accepted_too()
        {
            var stored = new PasswordHasher<LegacyUser>().HashPassword(new LegacyUser(), "Another-1");
            Assert.StartsWith("AQAAAAIAAYag", stored);
            Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<LegacyUser>().VerifyHashedPassword(new LegacyUser(), stored, "Another-1"));
        }

        // ASP.NET Core Identity 5.0's V3 format, byte for byte: 0x01, PRF 1 (HMACSHA256), 10 000 iterations,
        // 16-byte salt, 32-byte PBKDF2 subkey, all big-endian
        private static string Dotnet5Hash(string password)
        {
            var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            var subkey = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 10000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
            var output = new byte[13 + salt.Length + subkey.Length];
            output[0] = 0x01;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(5), 10000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(9), (uint)salt.Length);
            salt.CopyTo(output, 13);
            subkey.CopyTo(output, 13 + salt.Length);
            return Convert.ToBase64String(output);
        }
    }

    public class AttachmentStoreTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "qa-helpdesk-tests-" + Guid.NewGuid().ToString("N"));
        private readonly AttachmentStore _store;

        public AttachmentStoreTests() => _store = new AttachmentStore(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        private Task<AttachmentStore.Stored> Save(string name, byte[] content, long max = 1024 * 1024) =>
            _store.SaveAsync(name, new MemoryStream(content), max, CancellationToken.None);

        [Fact]
        public async Task A_real_screenshot_is_stored_with_its_hash()
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            var stored = await Save(@"C:\Users\x\Desktop\screen shot.png", png);

            Assert.Equal("screen shot.png", stored.FileName);
            Assert.Equal("image/png", stored.ContentType);
            Assert.Equal(64, stored.Sha256.Length);
            await using var file = _store.Open(stored.StoredPath);
            Assert.Equal(png.Length, file.Length);
        }

        [Theory]
        [InlineData("setup.exe")]
        [InlineData("page.html")]
        [InlineData("logo.svg")]
        [InlineData("run.ps1")]
        public async Task Files_that_could_run_are_refused(string name)
        {
            await Assert.ThrowsAsync<ValidationException>(() => Save(name, Encoding.UTF8.GetBytes("<script>alert(1)</script>")));
        }

        [Fact]
        public async Task A_program_renamed_to_an_image_is_refused()
        {
            var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };
            var error = await Assert.ThrowsAsync<ValidationException>(() => Save("invoice.pdf", exe));
            Assert.Contains("does not contain", error.Message);
        }

        [Fact]
        public async Task Binary_content_in_a_text_file_and_oversized_files_are_refused()
        {
            await Assert.ThrowsAsync<ValidationException>(() => Save("notes.txt", new byte[] { 0x41, 0x00, 0x42 }));
            await Assert.ThrowsAsync<ValidationException>(() => Save("big.log", new byte[2048], max: 1024));
            Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "attachments", "incoming")));
        }

        [Fact]
        public void Stored_paths_cannot_leave_the_attachment_folder()
        {
            Assert.Throws<NotFoundException>(() => _store.Open(@"..\..\Windows\win.ini"));
        }
    }
}
