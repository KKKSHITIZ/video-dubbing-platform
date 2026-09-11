using System.Threading.RateLimiting;
using OpenTelemetry.Metrics;
using Serilog;
using VideoDubbing.Api.Middleware;
using VideoDubbing.Api.Swagger;
using VideoDubbing.Application;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Infrastructure;
using VideoDubbing.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// 1. Serilog Logging Setup
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

// 2. Controller & Swagger API Setup
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "AI Video Dubbing Platform", Version = "v1" });
    c.OperationFilter<RequirementNameOperationFilter>();
});

// 3. Exception Handling, Problem Details & Health Checks
builder.Services.AddExceptionHandler<ExceptionMappingHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// 4. Custom Application & Infrastructure Layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// 5. Processing Options & Kestrel Limits Configuration
var processing = builder.Configuration.GetSection(ProcessingOptions.SectionName).Get<ProcessingOptions>() ?? new ProcessingOptions();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = processing.MaxUploadSizeMb * 1024L * 1024L);
// Multipart (IFormFile) bodies are otherwise capped at 128 MB by ASP.NET Core defaults.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = processing.MaxUploadSizeMb * 1024L * 1024L);

// 7. Rate Limiter Configuration
if (processing.RateLimitPermitLimit > 0 && builder.Configuration.GetValue("Security:EnableRateLimiting", true))
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = processing.RateLimitPermitLimit,
                    Window = TimeSpan.FromSeconds(processing.RateLimitWindowSeconds),
                    QueueLimit = 0
                }));
    });
}

// 8. OpenTelemetry & Metrics Instrumentation
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        metrics.AddPrometheusExporter();
    });

var app = builder.Build();

// 9. Database Auto-Migration / Initialization Scope
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DubbingDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// 10. HTTP Request Pipeline (Middleware stack)
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseMiddleware<ApiKeyMiddleware>();

if (processing.RateLimitPermitLimit > 0 && builder.Configuration.GetValue("Security:EnableRateLimiting", true))
{
    app.UseRateLimiter();
}

app.UseDefaultFiles();
app.UseStaticFiles();

// 11. Swagger Documentation UI
app.UseSwagger();
app.UseSwaggerUI();

// 12. Endpoint Route Mappings
app.MapControllers();
app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint("/metrics");

await app.RunAsync();

public partial class Program;
