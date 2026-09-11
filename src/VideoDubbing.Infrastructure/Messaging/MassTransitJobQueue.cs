using MassTransit;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Contracts.Messaging;

namespace VideoDubbing.Infrastructure.Messaging;

public sealed class MassTransitJobQueue : IJobQueue
{
    private readonly IPublishEndpoint _publish;

    public MassTransitJobQueue(IPublishEndpoint publish) => _publish = publish;

    public Task EnqueueAsync(Guid jobId, string correlationId, CancellationToken cancellationToken) =>
        _publish.Publish(new ProcessJobMessage(jobId, correlationId), cancellationToken);
}
