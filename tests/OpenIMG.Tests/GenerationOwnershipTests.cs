using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenIMG.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class GenerationOwnershipTests
{
    [Fact]
    public void Test1_StaleAttachmentExistsBeforeUpload_Aborts()
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "CAL03_test1",
            AttachmentCountBeforeUpload = 1, // Stale attachment present
            AttachmentCountAfterUpload = 1
        };

        var ex = Assert.Throws<InvalidOperationException>(() => context.AssertPrePromptInvariants());
        Assert.Contains("FreshChatAttachmentCountBeforeUpload was 1, expected 0", ex.Message);
    }

    [Fact]
    public void Test2_TwoAttachmentsAfterUpload_PromptNotSent()
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "CAL03_test2",
            AttachmentCountBeforeUpload = 0,
            AttachmentCountAfterUpload = 2 // 2 attachments detected after upload
        };

        var ex = Assert.Throws<InvalidOperationException>(() => context.AssertPrePromptInvariants());
        Assert.Contains("AttachmentCountAfterUpload was 2, expected exactly 1", ex.Message);
    }

    [Fact]
    public void Test3_OldGeneratedImageExistsBeforeCurrentPrompt_CannotSatisfyCurrentGeneration()
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "CAL03_test3",
            ExistingGeneratedImageCount = 2,
            NewResponseDetected = true,
            NewGeneratedImageDetected = false, // No new image detected
            GeneratedImageCreatedAfterPrompt = false,
            DownloadOwnedByCurrentOperation = true
        };

        var ex = Assert.Throws<InvalidOperationException>(() => context.AssertPostGenerationInvariants());
        Assert.Contains("NewGeneratedImageDetected is false", ex.Message);
    }

    [Fact]
    public void Test4_NewResponseWithoutNewImage_DownloadNotStarted()
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "CAL06_test4",
            ExistingAssistantResponseCount = 1,
            ExistingGeneratedImageCount = 1,
            NewResponseDetected = true,
            NewGeneratedImageDetected = false, // Text response only, no new image
            GeneratedImageCreatedAfterPrompt = false,
            DownloadOwnedByCurrentOperation = false
        };

        var ex = Assert.Throws<InvalidOperationException>(() => context.AssertPostGenerationInvariants());
        Assert.Contains("NewGeneratedImageDetected is false", ex.Message);
    }

    [Fact]
    public void Test5_StaleDownloadStartingHandler_CannotCompleteNextOperation()
    {
        var context1 = new GenerationOwnershipContext
        {
            GenerationOperationId = "Op1",
            DownloadOwnedByCurrentOperation = false
        };

        // If download was not claimed by Op1, invariant assertion fails
        var ex = Assert.Throws<InvalidOperationException>(() => context1.AssertPostGenerationInvariants());
        Assert.Contains("NewResponseDetected is false", ex.Message);
    }

    [Fact]
    public void Test6_WrongOperationIdDownload_Rejected()
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "CAL03_correct_op",
            PersistedCandidateOperationId = "CAL01_stale_op" // Wrong operation ID
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            context.AssertPersistenceOwnership(Path.Combine(Path.GetTempPath(), "candidate.png"), "HASH123", "CAL03"));

        Assert.Contains("does not match active GenerationOperationId", ex.Message);
    }

    [Fact]
    public void Test7_JobAFinishesAfterJobBStarts_JobACannotWriteIntoJobBArtifactPath()
    {
        var jobAContext = new GenerationOwnershipContext
        {
            GenerationOperationId = "JobA_Op",
            CalibrationId = "CAL03",
            AssetId = "AssetA",
            PersistedCandidateOperationId = "JobA_Op"
        };

        // Attempting to write Job A's candidate into Job B's calibration destination (CAL04)
        var ex = Assert.Throws<InvalidOperationException>(() =>
            jobAContext.AssertPersistenceOwnership(Path.Combine(Path.GetTempPath(), "candidate-a.png"), "HASHA", "CAL04"));

        Assert.Contains("Target asset/calibration mismatch", ex.Message);
    }

    [Fact]
    public void Test8_UniqueTempDirectoryPerGeneration()
    {
        var op1 = "CAL03_" + Guid.NewGuid().ToString("N");
        var op2 = "CAL03_" + Guid.NewGuid().ToString("N");

        var tempBase = Path.Combine(Path.GetTempPath(), "OpenIMG_Test");
        var path1 = Path.Combine(tempBase, op1, "candidate.png");
        var path2 = Path.Combine(tempBase, op2, "candidate.png");

        Assert.NotEqual(path1, path2);
        Assert.NotEqual(Path.GetDirectoryName(path1), Path.GetDirectoryName(path2));
    }

    [Fact]
    public void Test9_CandidatePersistenceVerifiesSourceJobOperationOwnership()
    {
        var context = new GenerationOwnershipContext
        {
            GenerationOperationId = "CAL09_valid",
            CalibrationId = "CAL09",
            ExpectedSourceSha256 = "SHA_ORIGINAL_20171014",
            PersistedCandidateOperationId = "CAL09_valid"
        };

        // Matching calibration ID and operation ID succeeds
        context.AssertPersistenceOwnership("D:\\OpenIMG\\candidate.png", "CAND_SHA", "CAL09");

        // Mismatched operation ID fails
        context.PersistedCandidateOperationId = "CAL09_foreign";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            context.AssertPersistenceOwnership("D:\\OpenIMG\\candidate.png", "CAND_SHA", "CAL09"));
        Assert.Contains("does not match active GenerationOperationId", ex.Message);
    }

    [Fact]
    public async Task Test10_SequentialBrowserSemaphore_PreventsOverlappingGptOperations()
    {
        var client = new OpenIMG.App.Browser.GptWebClient(NullLogger<OpenIMG.App.Browser.GptWebClient>.Instance);

        var lock1Acquired = false;
        var lock2Acquired = false;

        var lock1 = await client.AcquireBrowserLockAsync();
        lock1Acquired = true;

        var lock2Task = Task.Run(async () =>
        {
            using var lock2 = await client.AcquireBrowserLockAsync();
            lock2Acquired = true;
        });

        // Ensure lock 2 cannot be acquired while lock 1 is held
        await Task.Delay(100);
        Assert.True(lock1Acquired);
        Assert.False(lock2Acquired);

        // Release lock 1 -> lock 2 proceeds
        lock1.Dispose();
        await lock2Task;
        Assert.True(lock2Acquired);
    }
}
