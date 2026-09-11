using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;

namespace VideoDubbing.Api.Controllers;

/// <summary>Development diagnostics: reports the effective provider configuration.</summary>
[ApiController]
[Route("api/v1/diag")]
public sealed class DiagController : ControllerBase
{
    private readonly ProviderOptions _providers;
    private readonly AiSecretsOptions _secrets;

    public DiagController(
        IOptions<ProviderOptions> providers,
        IOptions<AiSecretsOptions> secrets)
    {
        _providers = providers.Value;
        _secrets = secrets.Value;
    }

    [HttpGet("providers")]
    public IActionResult Providers() => Ok(new
    {
        EnableFallback = _providers.EnableFallback,
        EnableCostAwareRouting = _providers.EnableCostAwareRouting,
        CostAwareShortJobSeconds = _providers.CostAwareShortJobSeconds,
        Translation = new { _providers.Translation.Primary, _providers.Translation.Fallbacks },
        Voice = new { _providers.Voice.Primary, _providers.Voice.Fallbacks },
        SpeechToText = new { _providers.SpeechToText.Primary, _providers.SpeechToText.Fallbacks },
        Secrets = new
        {
            DeepLConfigured = !string.IsNullOrWhiteSpace(_secrets.DeepLApiKey),
            ElevenLabsConfigured = !string.IsNullOrWhiteSpace(_secrets.ElevenLabsApiKey)
        }
    });
}