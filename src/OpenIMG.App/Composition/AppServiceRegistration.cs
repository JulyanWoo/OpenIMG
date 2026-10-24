using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIMG.App.Browser;
using OpenIMG.App.Services;
using OpenIMG.App.ViewModels;
using OpenIMG.Application.Abstractions.AI;
using OpenIMG.Application.Abstractions.Analysis;
using OpenIMG.Application.Abstractions.Orchestration;
using OpenIMG.Application.Abstractions.Persistence;
using OpenIMG.Application.Abstractions.Storage;
using OpenIMG.Application.Abstractions.Validation;
using OpenIMG.Application.Analysis;
#if OPENIMG_ENGINEERING
using OpenIMG.Application.Calibration;
#endif
using OpenIMG.Application.Configuration;
using OpenIMG.Application.Orchestration;
using OpenIMG.Application.Importing;
using OpenIMG.Application.Abstractions.Prompts;
using OpenIMG.Application.Restoration;
using OpenIMG.Application.Policies;
using OpenIMG.Application.Validation;
using OpenIMG.Infrastructure.Analysis;
using OpenIMG.Infrastructure.Configuration;
using OpenIMG.Infrastructure.ImageProcessing;
using OpenIMG.Infrastructure.Persistence;
using OpenIMG.Infrastructure.Storage;

namespace OpenIMG.App.Composition;

/// <summary>
/// Composition root for the desktop host.
/// Keeping registrations out of App.xaml.cs makes the startup lifecycle and
/// the service graph independently reviewable and reusable by future tools.
/// </summary>
internal static class AppServiceRegistration
{
    public static IServiceCollection AddOpenImgDesktopServices(
        this IServiceCollection services,
        OpenImgOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        // Local analysis and ONNX services consume IOptions<T>. Explicitly bind
        // the already-loaded settings instance so every consumer sees the same
        // paths and thresholds instead of a separately-created default object.
        services.AddSingleton<IOptions<OpenImgOptions>>(Options.Create(options));

        services.AddDbContextFactory<OpenImgDbContext>(dbOptions =>
            dbOptions.UseSqlite($"Data Source={options.DatabasePath}"));

        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IImageAssetRepository, ImageAssetRepository>();
        services.AddSingleton<IProviderUsageRepository, ProviderUsageRepository>();

        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ResultCopyService>();
        services.AddSingleton<ImageRetryService>();
        services.AddSingleton<ProjectSourceFolderMonitor>();
        services.AddSingleton<ProjectSourceImageImporter>();
        services.AddSingleton<ILocalImageValidator, OpenCvImageValidator>();
        services.AddSingleton<IComponentCardinalityValidator, OpenCvComponentCardinalityValidator>();
        services.AddSingleton<ValidationEvidenceClassifier>();
        services.AddSingleton<LocalDecisionPolicy>();
        services.AddSingleton<RemoteDecisionCombiner>();
        services.AddSingleton<ValidationDecisionEngine>();
        services.AddSingleton<IRemoteValidationPolicy, RemoteValidationPolicy>();

        services.AddSingleton<ILocalObjectDetector, OnnxObjectDetector>();
        services.AddSingleton<IStructuralRegionDetector, OpenCvStructuralRegionDetector>();
        services.AddSingleton<IColorRegionAnalyzer, OpenCvColorRegionAnalyzer>();
        services.AddSingleton<IHardwareCandidateDetector, OpenCvHardwareCandidateDetector>();
        services.AddSingleton<ILocalContractConflictDetector, LocalContractConflictDetector>();
        services.AddSingleton<ILocalContractBuilder, LocalFurnitureContractBuilder>();
        services.AddSingleton<IFurnitureAnalysisContractAdapter, FurnitureAnalysisContractAdapter>();
        services.AddSingleton<IProtectedAbsenceDetector, OpenCvProtectedAbsenceDetector>();
        services.AddSingleton<IPersistentSceneDetailDetector, OpenCvPersistentSceneDetailDetector>();
        services.AddSingleton<ISceneColorAnalyzer, OpenCvSceneColorAnalyzer>();
        services.AddSingleton<ILocalAnalysisEngine, LocalAnalysisEngine>();
        services.AddSingleton<IAnalysisComparator, AnalysisComparator>();
        services.AddSingleton<ILocalToFurnitureAnalysisAdapter, LocalToFurnitureAnalysisAdapter>();
        services.AddSingleton<ILocalPromptCompactor, LocalPromptCompactor>();
        services.AddSingleton<ICorrectiveRestorationPromptCompiler, CorrectiveRestorationPromptCompiler>();
        services.AddSingleton<CorrectiveDifferenceAnalyzer>();
        services.AddSingleton<DualModelRestorationPolicy>();
        services.AddSingleton<ProviderUsageBudget>();

#if OPENIMG_ENGINEERING
        if (options.EnableCalibrationFeatures)
        {
            services.AddSingleton<Phase4C1LiveCalibrationService>();
            services.AddSingleton<Phase4C2LiveCalibrationService>();
            services.AddSingleton<Phase4C2HistoricalRevalidationService>();
            services.AddSingleton<Phase4C3LiveCalibrationService>();
            services.AddSingleton<Phase4D1LiveVerificationService>();
            services.AddSingleton<Phase4EPilotService>();
            services.AddSingleton<Phase4E2LiveRestorationService>();
            services.AddSingleton<Phase4FLimitedArchivePilotService>();
            services.AddSingleton<Phase4GUnseenPilotService>();
            services.AddSingleton<Phase4G1MultiviewService>();
            services.AddSingleton<Phase4G2ScopedReferenceService>();
            services.AddSingleton<Phase4F1PreflightService>();
            services.AddSingleton<Phase4F1TargetedRecoveryService>();
            services.AddSingleton<Phase4F3PreflightService>();
            services.AddSingleton<Phase4F3MicroPilotService>();
        }
#endif

        services.AddSingleton<GptWebClient>();
        services.AddSingleton<IGptWebClient>(sp => sp.GetRequiredService<GptWebClient>());
        services.AddSingleton<GptWebProvider>();
        services.AddSingleton<IImageGenerationProvider>(sp => sp.GetRequiredService<GptWebProvider>());
        services.AddSingleton<ICorrectiveImageGenerationProvider>(sp => sp.GetRequiredService<GptWebProvider>());
#if OPENIMG_ENGINEERING
        if (options.EnableCalibrationFeatures)
        {
            services.AddSingleton<GptCorrectiveVerificationService>();
            services.AddSingleton<GptDirectRestorationPilotService>();
            services.AddSingleton<GptTwoImageCorrectiveSpikeService>();
            services.AddSingleton<GptPromptVerificationSpikeService>();
            services.AddSingleton<GptSpikeWindow>();
        }
#endif

        services.AddSingleton<IImageProcessingOrchestrator, ImageProcessingOrchestrator>();
        services.AddSingleton<IProcessingQueue, ProcessingQueue>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
