using System.IO;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIMG.App.Browser;
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Policies;
using OpenIMG.Core.Enums;
using OpenIMG.Infrastructure.Persistence;

namespace OpenIMG.AutomationCandidates;

internal static class ProductionProviderCheck
{
    public static async Task<CaseResult> RunAsync(string scenario)
    {
        var fixture = new OperationFixture();
        await File.WriteAllBytesAsync(fixture.SourcePath, [..Convert.FromBase64String(Program.Png), 0]);
        fixture.Context.ExpectedSourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath)));
        var factory = new PooledDbContextFactory<OpenImgDbContext>(new DbContextOptionsBuilder<OpenImgDbContext>()
            .UseSqlite($"Data Source={Path.Combine(fixture.Root, "provider-test.db")}").Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        await using var browser = await OfflineWebView.StartAsync(fixture.Root, scenario);
        var client = new GptWebClient(NullLogger<GptWebClient>.Instance);
        client.AttachWebView(browser.View);
        await browser.NavigateAsync("https://chatgpt.com/");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var provider = new GptWebProvider(client, new ProviderUsageRepository(factory, new ProviderUsageBudget()),
                new OpenImgOptions(), NullLogger<GptWebProvider>.Instance);
            var output = await provider.GenerateRestorationScopedAsync(fixture.SourcePath, Program.Prompt,
                Path.Combine(fixture.Root, "downloads"), fixture.Context, deadline.Token);
            var sends = int.Parse((await browser.ExecuteAsync("sessionStorage.sends", deadline.Token)).Trim('"'));
            var checkpoint = System.Text.Json.JsonSerializer.Deserialize<OpenIMG.Application.Restoration.GenerationRecoveryCheckpoint>(await File.ReadAllTextAsync(fixture.CheckpointPath))!;
            var valid = File.Exists(output) && checkpoint.DownloadedPath == output && checkpoint.DownloadedSha256 == fixture.Context.DownloadedCandidateSha256 &&
                fixture.Context.OperationState == ProviderOperationState.FileVerified && fixture.Context.CandidateEvidence is not null;
            return new(scenario, valid && sends == 1 && browser.Downloads == 1, valid, sends, browser.Downloads, fixture.Context.OperationState.ToString(), null);
        }
        catch (Exception ex) { return new(scenario, false, false, 0, browser.Downloads, fixture.Context.OperationState.ToString(), ex.GetBaseException().Message); }
        finally { client.DetachAndCancel(); }
    }
}
