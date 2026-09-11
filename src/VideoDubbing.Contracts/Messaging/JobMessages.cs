namespace VideoDubbing.Contracts.Messaging;

public sealed record ProcessJobMessage(Guid JobId, string CorrelationId);

public sealed record CancelJobMessage(Guid JobId, string CorrelationId);
