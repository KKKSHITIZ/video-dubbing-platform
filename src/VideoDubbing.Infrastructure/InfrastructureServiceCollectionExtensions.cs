using Amazon;
using Amazon.S3;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using VideoDubbing.Application.Storage;
using VideoDubbing.Application.VideoLocalization;
using VideoDubbing.Infrastructure.Media;
using VideoDubbing.Infrastructure.Localization;
using VideoDubbing.Infrastructure.Messaging;
using VideoDubbing.Infrastructure.Persistence;
using VideoDubbing.Infrastructure.Providers;
using VideoDubbing.Infrastructure.Providers.Diarization;
using VideoDubbing.Infrastructure.Providers.LipSync;
using VideoDubbing.Infrastructure.Providers.Speech;
using VideoDubbing.Infrastructure.Providers.Translation;
using VideoDubbing.Infrastructure.Providers.Voice;
using VideoDubbing.Infrastructure.Storage;

namespace VideoDubbing.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool includeMassTransit = true,
        Action<IBusRegistrationConfigurator>? configureBus = null)
    {
        services.Configure<ProcessingOptions>(configuration.GetSection(ProcessingOptions.SectionName));
        services.Configure<ProviderOptions>(configuration.GetSection(ProviderOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.Configure<MediaOptions>(configuration.GetSection(MediaOptions.SectionName));
        services.Configure<AiSecretsOptions>(configuration.GetSection(AiSecretsOptions.SectionName));
        services.Configure<VideoLocalizationOptions>(configuration.GetSection(VideoLocalizationOptions.SectionName));
        services.Configure<ElevenLabsOptions>(configuration.GetSection(ElevenLabsOptions.SectionName));

        services.AddHttpClient(ElevenLabsDubbingService.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ElevenLabsOptions>>().Value;
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(options.BaseUrl) ? "https://api.elevenlabs.io" : options.BaseUrl);
            var apiKey = options.ApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Add("xi-api-key", apiKey);
            }

            client.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddSingleton<ElevenLabsDubbingApi>();
        services.AddSingleton<ElevenLabsMusicApi>();

        var dbProvider = configuration["Database:Provider"] ?? "Postgres";
        if (dbProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<DubbingDbContext>(o => o.UseInMemoryDatabase(configuration["Database:Name"] ?? "videodubbing"));
        }
        else if (dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("Sqlite")
                                   ?? "Data Source=videodubbing.db";
            services.AddDbContext<DubbingDbContext>(o => o.UseSqlite(connectionString));
        }
        else
        {
            var connectionString = configuration.GetConnectionString("Postgres")
                                   ?? "Host=localhost;Port=5432;Database=videodubbing;Username=postgres;Password=postgres";
            services.AddDbContext<DubbingDbContext>(o => o.UseNpgsql(connectionString));
        }
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddSingleton<IJobProgressPublisher, LoggingProgressPublisher>();

        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        if (storage.Provider.Equals("S3", StringComparison.OrdinalIgnoreCase) ||
            storage.Provider.Equals("MinIO", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAmazonS3>(_ =>
            {
                var config = new AmazonS3Config
                {
                    ServiceURL = storage.S3.ServiceUrl,
                    ForcePathStyle = storage.S3.ForcePathStyle,
                    AuthenticationRegion = storage.S3.Region
                };
                return new AmazonS3Client(storage.S3.AccessKey, storage.S3.SecretKey, config);
            });
            services.AddSingleton<IObjectStorage>(sp => new S3ObjectStorage(sp.GetRequiredService<IAmazonS3>(), storage));
        }
        else
        {
            services.AddSingleton<IObjectStorage>(_ => new LocalObjectStorage(storage));
        }

        services.AddSingleton<IMediaProcessor, FfmpegMediaProcessor>();
        services.AddHttpClient();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiSecretsOptions>>().Value);

        services.AddSingleton<IDiarizationProvider, FfmpegSilenceDiarizationProvider>();
        services.AddSingleton<IDiarizationProvider, PyannoteDiarizationProvider>();
        services.AddSingleton<ISpeechToTextProvider, MockSpeechToTextProvider>();
        services.AddHttpClient<VoskSpeechToTextProvider>();
        services.AddTransient<ISpeechToTextProvider>(sp => sp.GetRequiredService<VoskSpeechToTextProvider>());
        services.AddHttpClient<WhisperSpeechToTextProvider>();
        services.AddTransient<ISpeechToTextProvider>(sp => sp.GetRequiredService<WhisperSpeechToTextProvider>());
        services.AddHttpClient<OpenAiWhisperSpeechToTextProvider>();
        services.AddTransient<ISpeechToTextProvider>(sp => sp.GetRequiredService<OpenAiWhisperSpeechToTextProvider>());
        services.AddHttpClient<DeepgramSpeechToTextProvider>();
        services.AddTransient<ISpeechToTextProvider>(sp => sp.GetRequiredService<DeepgramSpeechToTextProvider>());
        services.AddSingleton<ISpeechToTextProvider, AssemblyAiSpeechToTextProvider>();
        services.AddSingleton<ISpeechToTextProvider, GoogleSpeechToTextProvider>();
        services.AddSingleton<ITranslationProvider, MockTranslationProvider>();
        services.AddHttpClient<OpenAiTranslationProvider>();
        services.AddTransient<ITranslationProvider>(sp => sp.GetRequiredService<OpenAiTranslationProvider>());
        services.AddSingleton<ITranslationProvider, GeminiTranslationProvider>();
        services.AddSingleton<ITranslationProvider, ClaudeTranslationProvider>();
        services.AddHttpClient<DeepLTranslationProvider>();
        services.AddTransient<ITranslationProvider>(sp => sp.GetRequiredService<DeepLTranslationProvider>());
        services.AddHttpClient<MyMemoryTranslationProvider>();
        services.AddTransient<ITranslationProvider>(sp => sp.GetRequiredService<MyMemoryTranslationProvider>());
        services.AddHttpClient<GoogleGenXTranslationProvider>();
        services.AddTransient<ITranslationProvider>(sp => sp.GetRequiredService<GoogleGenXTranslationProvider>());
        services.AddSingleton<IVoiceProvider, LocalFfmpegVoiceProvider>();
        services.AddSingleton<IVoiceProvider, SapiVoiceProvider>();
        services.AddHttpClient<ElevenLabsVoiceProvider>();
        services.AddTransient<IVoiceProvider>(sp => sp.GetRequiredService<ElevenLabsVoiceProvider>());
        services.AddHttpClient<GoogleTranslateTtsVoiceProvider>();
        services.AddTransient<IVoiceProvider>(sp => sp.GetRequiredService<GoogleTranslateTtsVoiceProvider>());
        services.AddSingleton<EdgeTtsVoiceProvider>();
        services.AddTransient<IVoiceProvider>(sp => sp.GetRequiredService<EdgeTtsVoiceProvider>());
        services.AddHttpClient<AzureSpeechVoiceProvider>();
        services.AddTransient<IVoiceProvider>(sp => sp.GetRequiredService<AzureSpeechVoiceProvider>());
        services.AddSingleton<IVoiceProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VideoDubbing.Application.Configuration.ProviderOptions>>().Value;
            return new CoquiVoiceProvider(options.CoquiCliPath);
        });
        services.AddSingleton<IVoiceProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VideoDubbing.Application.Configuration.ProviderOptions>>().Value;
            return new OpenVoiceProvider(options.OpenVoiceCliPath);
        });
        services.AddSingleton<IVoiceProvider, OpenSourceCliVoiceProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VideoDubbing.Application.Configuration.ProviderOptions>>().Value;
            return new OpenSourceCliVoiceProvider(options.VoiceCliPath);
        });
        services.AddSingleton<IVoiceProfileRegistry, VoiceProfileRegistry>();
        services.AddSingleton<ILipSyncEngine>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<VideoDubbing.Application.Configuration.ProviderOptions>>().Value;
            return string.IsNullOrWhiteSpace(options.LipSyncCliPath)
                ? new PassthroughLipSyncEngine()
                : new CliLipSyncEngine(options.LipSyncCliPath);
        });
        services.AddSingleton<IProviderResolver, ProviderResolver>();

        services.AddHttpClient<IVideoTranslationManager, VideoTranslationManager>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<VideoLocalizationOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(30, options.HttpTimeoutSeconds));
            client.Timeout = timeout;
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            }

            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddHttpClient("VideoTranslation", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<VideoLocalizationOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            }

            client.Timeout = TimeSpan.FromSeconds(Math.Max(30, options.HttpTimeoutSeconds));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddSingleton<VideoTranslationService>();

        var messaging = configuration["Messaging:Provider"] ?? "RabbitMq";
        if (includeMassTransit && messaging.Equals("RabbitMq", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IJobQueue, MassTransitJobQueue>();
            services.AddMassTransit(x =>
            {
                x.SetKebabCaseEndpointNameFormatter();
                configureBus?.Invoke(x);
                x.UsingRabbitMq((context, cfg) =>
                {
                    var host = configuration.GetConnectionString("RabbitMq") ?? "amqp://guest:guest@localhost:5672";
                    cfg.Host(host);
                    cfg.PrefetchCount = configuration.GetValue("Processing:MaxConcurrentJobs", 4);
                    cfg.UseMessageRetry(r => r.Interval(configuration.GetValue("Processing:RetryCount", 3), TimeSpan.FromSeconds(5)));
                    cfg.ConfigureEndpoints(context);
                });
            });
        }
        else
        {
            services.AddScoped<IJobQueue, NoOpJobQueue>();
        }

        return services;
    }
}
