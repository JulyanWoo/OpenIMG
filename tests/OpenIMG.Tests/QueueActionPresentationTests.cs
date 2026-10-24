using OpenIMG.App.ViewModels;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public sealed class QueueActionPresentationTests
{
    [Fact]
    public void StoppedReadyQueueCanStartAndDoesNotOfferPauseOrCancel()
    {
        var state = QueueActionPresentation.Create(
            hasProject: true,
            running: false,
            paused: false,
            gptReady: true,
            actionsAvailable: true,
            hasCurrentAsset: false,
            pendingCount: 7);

        Assert.Equal("Procesar pendientes", state.PrimaryLabel);
        Assert.True(state.CanRunPrimary);
        Assert.False(state.CanPause);
        Assert.False(state.CanCancel);
    }

    [Fact]
    public void PausedQueueResumesFromPrimaryActionWithoutShowingSecondResumeAction()
    {
        var state = QueueActionPresentation.Create(
            hasProject: true,
            running: true,
            paused: true,
            gptReady: true,
            actionsAvailable: true,
            hasCurrentAsset: true,
            pendingCount: 4);

        Assert.Equal("Continuar cola", state.PrimaryLabel);
        Assert.True(state.CanRunPrimary);
        Assert.Equal("En pausa", state.PauseLabel);
        Assert.False(state.CanPause);
        Assert.True(state.CanCancel);
    }

    [Fact]
    public void StoppedQueueAfterFailureCanBeStartedAgain()
    {
        var state = QueueActionPresentation.Create(
            hasProject: true,
            running: false,
            paused: false,
            gptReady: true,
            actionsAvailable: true,
            hasCurrentAsset: false,
            pendingCount: 3);

        Assert.True(state.CanRunPrimary);
        Assert.Equal("Procesar pendientes", state.PrimaryLabel);
    }

    [Fact]
    public void PrimaryActionOpensGptWebBeforeProcessingWhenSessionIsNotReady()
    {
        var state = QueueActionPresentation.Create(
            hasProject: true,
            running: false,
            paused: false,
            gptReady: false,
            actionsAvailable: true,
            hasCurrentAsset: false,
            pendingCount: 3);

        Assert.Equal("Conectar GPT", state.PrimaryLabel);
        Assert.True(state.CanRunPrimary);
        Assert.Contains("GPT Web", state.PrimaryHint, StringComparison.Ordinal);
        Assert.Contains("no enviará imágenes", state.PrimaryHint, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedQueueDoesNotOfferAnEnabledNoOpStart()
    {
        var state = QueueActionPresentation.Create(
            hasProject: true,
            running: false,
            paused: false,
            gptReady: true,
            actionsAvailable: true,
            hasCurrentAsset: false,
            pendingCount: 0);

        Assert.Equal("Todo procesado", state.PrimaryLabel);
        Assert.False(state.CanRunPrimary);
    }
}
