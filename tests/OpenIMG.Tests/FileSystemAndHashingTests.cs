using System.IO;
using OpenIMG.Infrastructure.Storage;
using Xunit;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Integration)]
public class FileSystemAndHashingTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileSystemService _fileService;

    public FileSystemAndHashingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"OpenIMG_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _fileService = new FileSystemService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task ComputeSha256_ComputesDeterministicHash()
    {
        var testFile = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "OpenIMG Immutable Test Content");

        var hash1 = await _fileService.ComputeSha256Async(testFile);
        var hash2 = await _fileService.ComputeSha256Async(testFile);

        Assert.NotNull(hash1);
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task VerifySourceIntegrity_MatchesOriginalFile()
    {
        var testFile = Path.Combine(_testDir, "furniture.jpg");
        await File.WriteAllBytesAsync(testFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03 });

        var originalHash = await _fileService.ComputeSha256Async(testFile);

        var isIntact = _fileService.VerifySourceIntegrity(testFile, originalHash);
        Assert.True(isIntact);

        // Simulate unauthorized file modification
        await File.WriteAllBytesAsync(testFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x99, 0x99, 0x99 });
        var isTampered = _fileService.VerifySourceIntegrity(testFile, originalHash);
        Assert.False(isTampered);
    }

    [Fact]
    public void EnsureProjectOutputDirectories_CreatesAllRequiredFolders()
    {
        var projectOut = Path.Combine(_testDir, "ProjectOut");
        _fileService.EnsureProjectOutputDirectories(projectOut);

        Assert.True(Directory.Exists(Path.Combine(projectOut, "Accepted")));
        Assert.True(Directory.Exists(Path.Combine(projectOut, "Review")));
        Assert.True(Directory.Exists(Path.Combine(projectOut, "Failed")));
        Assert.True(Directory.Exists(Path.Combine(projectOut, "Metadata")));
        Assert.True(Directory.Exists(Path.Combine(projectOut, "Diagnostics")));
        Assert.True(Directory.Exists(Path.Combine(projectOut, "Temp")));
    }
}
