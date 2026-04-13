using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using QualityAviationCodeRepositoryManagmentApi.API.Models;
using QualityAviationCodeRepositoryManagmentApi.API.Services;
using System.IO.Compression;

namespace QualityAviationCodeRepositoryManagmentApi.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RepositoryController : ControllerBase
    {
        private readonly IRepositoryService _repoService;
        private readonly IFileService _fileService;
        private readonly IConfiguration _configuration;
        public RepositoryController(IRepositoryService repoService, IFileService fileService, IConfiguration configuration)
        {
            _repoService = repoService;
            _fileService = fileService;
            _configuration = configuration;

        }

        private string GetUsername()
        {
            return HttpContext.Items["Username"]?.ToString();
        }

        [HttpPost("create")]
        public IActionResult CreateRepository([FromBody] dynamic request)
        {
            try
            {
                var username = GetUsername();
                string repoName = request.GetProperty("repoName").GetString();
                string description = request.GetProperty("description").GetString();

                _repoService.CreateRepository(username, repoName, description);

                return Ok(new ApiResponse
                {
                    Success = true,
                    Message = $"Repository '{repoName}' created successfully",
                    StatusCode = 200
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }

        //[HttpPost("push")]
        //[DisableRequestSizeLimit]
        //public async Task<IActionResult> Push([FromForm] IFormFile archiveFile, [FromForm] string repoName, [FromForm] string message)
        //{
        //    var startTime = DateTime.Now;
        //    var username = GetUsername();
        //    var tempDir = Path.Combine(Path.GetTempPath(), "QA_Uploads", Guid.NewGuid().ToString());

        //    try
        //    {
        //        Console.WriteLine($"[LOG {DateTime.Now:T}] ?? Push started for User: {username}, Repo: {repoName}");

        //        if (archiveFile == null || archiveFile.Length == 0)
        //            return BadRequest(new ApiResponse { Success = false, Message = "No archive received." });

        //        _fileService.EnsureDirectoryExists(tempDir);
        //        var tempZipPath = Path.Combine(tempDir, "incoming.zip");

        //        // Stream file to disk to save memory
        //        using (var stream = new FileStream(tempZipPath, FileMode.Create))
        //        {
        //            await archiveFile.CopyToAsync(stream);
        //        }

        //        Console.WriteLine($"[LOG] Received {archiveFile.Length / 1024} KB. Extracting and validating...");

        //        // Secure extraction preserving all binaries (DLLs, etc.)
        //        using (var archive = ZipFile.OpenRead(tempZipPath))
        //        {
        //            var filesToPush = new List<FileUpload>();
        //            foreach (var entry in archive.Entries)
        //            {
        //                Console.WriteLine($"   -> Received: {entry.FullName} ({entry.Length} bytes)");
        //                if (string.IsNullOrEmpty(entry.Name)) continue; // Skip directories

        //                using (var entryStream = entry.Open())
        //                using (var ms = new MemoryStream())
        //                {
        //                    await entryStream.CopyToAsync(ms);
        //                    var content = ms.ToArray();

        //                    filesToPush.Add(new FileUpload
        //                    {
        //                        FileName = entry.FullName,
        //                        FileContent = content,
        //                        FileHash = _fileService.ComputeFileHash(content)
        //                    });
        //                }
        //            }

        //            _repoService.PushFiles(username, repoName, message, filesToPush);
        //        }

        //        var duration = DateTime.Now - startTime;
        //        Console.WriteLine($"[SUCCESS] Push completed in {duration.TotalSeconds:F2}s. Version created.");

        //        return Ok(new ApiResponse { Success = true, Message = "Push successful", StatusCode = 200 });
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"[ERROR] Push failed: {ex.Message}");
        //        return BadRequest(new ApiResponse { Success = false, Message = ex.Message });
        //    }
        //    finally
        //    {
        //        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        //    }
        //}

        [HttpPost("push")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> Push([FromForm] IFormFile archiveFile, [FromForm] string repoName, [FromForm] string owner, [FromForm] string message)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            try
            {
                var currentUser = GetUsername();
                var targetOwner = string.IsNullOrEmpty(owner) ? currentUser : owner;

                Console.WriteLine($"[LOG {DateTime.Now:T}] User {currentUser} pushing to {targetOwner}/{repoName}");

                if (archiveFile == null || archiveFile.Length == 0) return BadRequest("Empty file");

                Directory.CreateDirectory(tempDir);
                var tempZip = Path.Combine(tempDir, "temp.zip");

                using (var stream = new FileStream(tempZip, FileMode.Create))
                    await archiveFile.CopyToAsync(stream);

                var files = new List<FileUpload>();
                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        using var ms = new MemoryStream();
                        entry.Open().CopyTo(ms);
                        var content = ms.ToArray();

                        files.Add(new FileUpload
                        {
                            FileName = entry.FullName,
                            FileContent = content,
                            FileHash = _fileService.ComputeFileHash(content)
                        });
                        // Detailed Server Log
                        Console.WriteLine($"   -> Storing: {entry.FullName}");
                    }
                }

                _repoService.PushFiles(targetOwner, repoName, message, files);
                return Ok(new ApiResponse { Success = true, Message = "Push successful" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
                return BadRequest(new ApiResponse { Success = false, Message = ex.Message });
            }
            finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
        }
        [HttpGet("pull")]
        public IActionResult Pull(string owner, string repoName, string versionName = null)
        {
            try
            {
                var fileContent = _repoService.PullFiles(owner, repoName, versionName);

                return File(fileContent, "application/zip", $"{repoName}_{versionName ?? "latest"}.zip");
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }

        [HttpGet("clone")]
        public IActionResult Clone(string owner, string repoName)
        {
            try
            {
                var fileContent = _repoService.CloneRepository(owner, repoName);

                return File(fileContent, "application/zip", $"{repoName}-clone.zip");
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }

        [HttpGet("list")]
        public IActionResult ListRepositories()
        {
            try
            {
                var username = GetUsername();
                var ownedRepos = _repoService.GetUserRepositories(username);
                var allRepos = _repoService.GetAllAvailableRepositories(username);
                var clonedRepos = allRepos.Where(r => r.Owner != username).ToList();

                return Ok(new ApiResponse<ListReposResponse>
                {
                    Success = true,
                    Message = "Repositories retrieved successfully",
                    StatusCode = 200,
                    Data = new ListReposResponse
                    {
                        OwnedRepos = ownedRepos,
                        ClonedRepos = clonedRepos
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }

        [HttpGet("versions")]
        public IActionResult GetVersions(string owner, string repoName)
        {
            try
            {
                var versions = _repoService.GetAllVersions(owner, repoName);

                return Ok(new ApiResponse<List<RepositoryVersion>>
                {
                    Success = true,
                    Message = "Versions retrieved successfully",
                    StatusCode = 200,
                    Data = versions
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }

        [HttpGet("latest")]
        public IActionResult GetLatestVersion(string owner, string repoName)
        {
            try
            {
                var version = _repoService.GetLatestVersion(owner, repoName);

                if (version == null)
                {
                    return NotFound(new ApiResponse
                    {
                        Success = false,
                        Message = "No versions found",
                        StatusCode = 404
                    });
                }

                return Ok(new ApiResponse<RepositoryVersion>
                {
                    Success = true,
                    Message = "Latest version retrieved successfully",
                    StatusCode = 200,
                    Data = version
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }

        [HttpGet("exists")]
        public IActionResult CheckRepository(string owner, string repoName)
        {
            try
            {
                var exists = _repoService.RepositoryExists(owner, repoName);

                return Ok(new ApiResponse
                {
                    Success = exists,
                    Message = exists ? "Repository exists" : "Repository does not exist",
                    StatusCode = exists ? 200 : 404
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = ex.Message,
                    StatusCode = 400
                });
            }
        }
    }
}
