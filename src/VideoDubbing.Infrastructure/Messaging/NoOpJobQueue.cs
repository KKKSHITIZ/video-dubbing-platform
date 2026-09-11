using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Pipeline;

namespace VideoDubbing.Infrastructure.Messaging;

/// <summary>
/// A no-op queue. When <c>Messaging:NoOpProcessInProcess</c> is enabled, jobs are instead
/// processed in-process within the publishing host — providing a zero-infrastructure demo
/// where a single process runs the whole pipeline without RabbitMQ.
/// </summary>
public sealed class NoOpJobQueue : IJobQueue
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _processInProcess;

    public NoOpJobQueue(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _processInProcess = configuration.GetValue("Messaging:NoOpProcessInProcess", false);
    }

    public Task EnqueueAsync(Guid jobId, string correlationId, CancellationToken cancellationToken)
    {
        if (!_processInProcess)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IPipelineOrchestrator>();
                await orchestrator.ProcessAsync(jobId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"In-process job processing failed: {ex.Message}");
            }
        });

        return Task.CompletedTask;
    }
}
