using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace VideoDubbing.Api.Swagger;

/// <summary>
/// Shows the assignment's requirement names as the operation title in Swagger UI
/// without changing any routes or code behavior.
/// </summary>
public sealed class RequirementNameOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var label = context.MethodInfo.Name switch
        {
            nameof(Controllers.JobsController.Upload) => "Upload video",
            nameof(Controllers.JobsController.GetStatus) => "Get processing status",
            nameof(Controllers.JobsController.GetTranscript) => "Retrieve transcripts",
            nameof(Controllers.JobsController.DownloadVideo) => "Download dubbed video",
            nameof(Controllers.JobsController.DownloadSubtitles) => "Download subtitles",
            nameof(Controllers.JobsController.DownloadVtt) => "Download VTT subtitles",
            nameof(Controllers.JobsController.GetLogs) => "Get processing logs",
            nameof(Controllers.JobsController.Retry) => "Retry failed processing",
            nameof(Controllers.JobsController.Cancel) => "Cancel processing",
            nameof(Controllers.JobsController.StreamEvents) => "Stream live progress",
            _ => null
        };

        if (label is not null)
        {
            operation.Summary = label;
        }
    }
}