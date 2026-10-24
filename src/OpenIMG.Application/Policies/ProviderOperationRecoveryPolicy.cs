using OpenIMG.Core.Enums;

namespace OpenIMG.Application.Policies;

/// <summary>
/// A persisted checkpoint at or after the send boundary may represent work
/// already accepted by the remote provider and must not be replayed blindly.
/// </summary>
public static class ProviderOperationRecoveryPolicy
{
    public static bool MayHaveSubmittedGeneration(ProviderOperationState state) =>
        state is ProviderOperationState.GenerationMayExist or
            ProviderOperationState.GenerationSubmitted or
            ProviderOperationState.CandidateDownloaded or
            ProviderOperationState.NeedsRecovery or ProviderOperationState.ConversationBound or
            ProviderOperationState.Generating or ProviderOperationState.CandidateDetected or
            ProviderOperationState.CandidateOwned or ProviderOperationState.ViewerOpened or
            ProviderOperationState.DownloadStarted or ProviderOperationState.FileVerified;
}
