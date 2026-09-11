using Microsoft.Extensions.DependencyInjection;
using VideoDubbing.Application.Jobs;
using VideoDubbing.Application.Pipeline;
using VideoDubbing.Application.Pipeline.Steps;

namespace VideoDubbing.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IJobService, JobService>();
        services.AddScoped<IPipelineOrchestrator, PipelineOrchestrator>();
        services.AddScoped<IPipelineStep, ExtractAudioStep>();
        services.AddScoped<IPipelineStep, DiarizeStep>();
        services.AddScoped<IPipelineStep, TranscribeStep>();
        services.AddScoped<IPipelineStep, TranslateStep>();
        services.AddScoped<IPipelineStep, SynthesizeStep>();
        services.AddScoped<IPipelineStep, SynchronizeAndExportStep>();
        return services;
    }
}
