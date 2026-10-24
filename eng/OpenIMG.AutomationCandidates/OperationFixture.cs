using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenIMG.Application.Restoration;
using OpenIMG.Core.Entities;
using OpenIMG.Core.Enums;
using OpenIMG.Core.Models;

namespace OpenIMG.AutomationCandidates;

internal sealed class OperationFixture
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIMG-AutomationLab", Guid.NewGuid().ToString("N"));
    public GenerationOwnershipContext Context { get; private set; }
    public GptOperationLifecycle Lifecycle { get; private set; }
    public string CheckpointPath => Path.Combine(Root, "checkpoint.json");
    public string SourcePath => Path.Combine(Root, "source.png");
    public string Destination => Path.Combine(Root, "operation", "result.png");
    private readonly Guid _assetId = Guid.NewGuid();
    public OperationFixture()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(SourcePath, Convert.FromBase64String(Program.Png));
        Context = new GenerationOwnershipContext
        {
            GenerationOperationId = "fixture-" + Guid.NewGuid().ToString("N"), AssetId = _assetId.ToString(), ProviderName = "GPT",
            ExpectedSourcePath = SourcePath, ExpectedSourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SourcePath))),
            BaselineAssistantResponseCount = 0
        };
        Lifecycle = CreateLifecycle();
    }
    private GptOperationLifecycle CreateLifecycle()
    {
        Context.PersistOperationStateAsync = async (_, ct) =>
        {
            var temporary = CheckpointPath + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(GenerationRecoveryCheckpoint.Capture(Context, Program.Prompt));
                await file.WriteAsync(bytes, ct);file.Flush(flushToDisk: true);
            }
            File.Move(temporary, CheckpointPath, overwrite: true);
        };
        return new GptOperationLifecycle(Context, Program.Prompt);
    }
    public void Restore()
    {
        var saved = File.ReadAllText(CheckpointPath);
        Context = GenerationRecoveryCheckpoint.Restore(
            new RestorationAttempt { ProviderOperationId = Context.GenerationOperationId, RestorationPrompt = Program.Prompt, RawResponse = saved },
            new ImageAsset { Id = _assetId, OriginalFilePath = SourcePath }, Context.ExpectedSourceSha256)
            ?? throw new InvalidOperationException("Durable fixture checkpoint could not be restored.");
        Lifecycle = CreateLifecycle();
    }
    public async Task VerifyFileAsync()
    {
        var bytes = await File.ReadAllBytesAsync(Destination);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
            throw new InvalidDataException("Invalid downloaded image signature.");
        if (!bytes.SequenceEqual(Convert.FromBase64String(Program.Png))) throw new InvalidDataException("Wrong candidate bytes.");
        Context.DownloadedTempPath = Destination;Context.DownloadedCandidateSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        await Lifecycle.AdvanceAsync(ProviderOperationState.CandidateDownloaded, CancellationToken.None);
        await Lifecycle.AdvanceAsync(ProviderOperationState.FileVerified, CancellationToken.None);
    }
}
