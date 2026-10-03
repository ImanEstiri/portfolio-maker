using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CvMaker.Api.Services.Storage;

/// <summary>
/// Reports whether generated PDFs can actually be stored.
///
/// <see cref="IArtifactStorageService.IsConfigured"/> is false when neither setting was
/// supplied <b>and</b> when the client failed to initialise — the emulator rejecting the
/// SDK's REST API version, a bad connection string, an unreachable endpoint. All of those
/// used to surface identically: the stack came up healthy, and every generation failed at
/// the last step with <c>StorageFailure</c>.
///
/// This is a constructor-time fact rather than a live probe. It is deliberately not a round
/// trip to the storage account: a health check that writes a blob on every poll costs money
/// in a deployed environment and would make the check itself the most frequent writer.
/// </summary>
public sealed class ArtifactStorageHealthCheck(IArtifactStorageService storage) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(storage.IsConfigured
            ? HealthCheckResult.Healthy("Artifact storage is configured.")
            : HealthCheckResult.Unhealthy(
                "Artifact storage is not configured or failed to initialise. Generated PDFs " +
                "cannot be saved, so every generation will fail with StorageFailure. Check the " +
                "startup log for the initialisation error."));
}
