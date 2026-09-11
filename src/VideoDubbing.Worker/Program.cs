using MassTransit;
using VideoDubbing.Application;
using VideoDubbing.Application.Pipeline;
using VideoDubbing.Contracts.Messaging;
using VideoDubbing.Infrastructure;
using VideoDubbing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(cfg => cfg.WriteTo.Console());
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, includeMassTransit: true, bus =>
{
    bus.AddConsumer<ProcessJobConsumer>();
});

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DubbingDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await host.RunAsync();

public sealed class ProcessJobConsumer : IConsumer<ProcessJobMessage>
{
    private readonly IPipelineOrchestrator _orchestrator;
    private readonly ILogger<ProcessJobConsumer> _logger;

    public ProcessJobConsumer(IPipelineOrchestrator orchestrator, ILogger<ProcessJobConsumer> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProcessJobMessage> context)
    {
        _logger.LogInformation("Processing job {JobId}", context.Message.JobId);
        await _orchestrator.ProcessAsync(context.Message.JobId, context.CancellationToken);
    }
}
