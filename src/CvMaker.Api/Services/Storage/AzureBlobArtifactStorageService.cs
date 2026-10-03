using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CvMaker.Api.Services.Storage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IArtifactStorageService"/>.
/// Container: "generated-cvs".
/// Path pattern: {userId}/{jobId}/{fileName}
///
/// Two ways to point at a storage account, checked in this order:
///  - <c>ArtifactsStorage:ConnectionString</c> — a connection string, for the
///    Azurite emulator in docker-compose.yml. The container is created on
///    startup since nothing else provisions it for local dev.
///  - <c>ArtifactsStorage:StorageAccountUrl</c> — a blob endpoint URL, used
///    with <see cref="DefaultAzureCredential"/> in deployed environments
///    (managed identity in Azure, or the AZURE_CLIENT_ID / AZURE_TENANT_ID /
///    AZURE_CLIENT_SECRET service-principal env vars on Fly.io — see
///    flyio/api.fly.toml). The container is expected to already exist: Bicep
///    (infra/) provisions it rather than the app.
///
/// Falls back to a no-op when neither is configured, so a job still fails
/// loudly (FailureCause.StorageFailure) rather than reporting success with a
/// PDF nobody can download.
/// </summary>
public sealed class AzureBlobArtifactStorageService : IArtifactStorageService
{
    private const string ContainerName = "generated-cvs";

    private readonly BlobContainerClient? _containerClient;
    private readonly ILogger<AzureBlobArtifactStorageService> _logger;

    public bool IsConfigured => _containerClient != null;

    public AzureBlobArtifactStorageService(
        IConfiguration configuration,
        ILogger<AzureBlobArtifactStorageService> logger)
    {
        _logger = logger;

        var connectionString = configuration["ArtifactsStorage:ConnectionString"];
        var storageAccountUrl = configuration["ArtifactsStorage:StorageAccountUrl"];

        // Built into a local first, and only handed to the field on full
        // success — a CreateIfNotExists failure partway through must leave
        // IsConfigured false, not a half-initialized client.
        BlobContainerClient? containerClient = null;

        try
        {
            var clientOptions = BuildClientOptions(configuration, logger);

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                var serviceClient = new BlobServiceClient(connectionString, clientOptions);
                var candidate = serviceClient.GetBlobContainerClient(ContainerName);
                // Nothing external provisions the container against the Azurite
                // emulator, unlike a real deployment's pre-created container.
                // Retried with backoff: docker-compose starts this container
                // and Azurite concurrently, so the first attempts can land
                // before Azurite is accepting connections.
                CreateContainerWithRetry(candidate);
                containerClient = candidate;
                _logger.LogDebug(
                    "AzureBlobArtifactStorageService initialized from connection string. Container: {Container}",
                    ContainerName);
            }
            else if (!string.IsNullOrWhiteSpace(storageAccountUrl))
            {
                var serviceClient = new BlobServiceClient(
                    new Uri(storageAccountUrl), new DefaultAzureCredential(), clientOptions);
                containerClient = serviceClient.GetBlobContainerClient(ContainerName);
                _logger.LogDebug(
                    "AzureBlobArtifactStorageService initialized. Account: {Url}, Container: {Container}",
                    storageAccountUrl, ContainerName);
            }
            else
            {
                _logger.LogWarning(
                    "Neither ArtifactsStorage:ConnectionString nor ArtifactsStorage:StorageAccountUrl is configured. " +
                    "Generated PDFs will not be persisted; generation jobs will fail with FailureCause.StorageFailure.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize AzureBlobArtifactStorageService. Generated PDFs will not be persisted.");
            containerClient = null;
        }

        _containerClient = containerClient;
    }

    /// <summary>
    /// Pins the REST API version the client negotiates, when
    /// <c>ArtifactsStorage:ServiceVersion</c> says to.
    ///
    /// The SDK otherwise asks for the newest version it knows, and an emulator only
    /// understands versions up to whatever it shipped with. Azure.Storage.Blobs 12.25.0 asks
    /// for <c>2025-07-05</c>; Azurite 3.34.0 answers 400 <c>InvalidHeaderValue</c>. That
    /// failure lands in the constructor, so <see cref="IsConfigured"/> stays false and every
    /// generation afterwards fails with <c>StorageFailure</c> — a long way from the cause.
    ///
    /// Left unset in deployed environments, where real Azure understands whatever the SDK
    /// asks for and pinning would only mean falling behind. It is docker-compose that sets it,
    /// because that is where the emulator is.
    ///
    /// A bad value warns and falls back to the SDK default rather than failing startup: this
    /// is a compatibility shim, and it should not be able to take the API down.
    /// </summary>
    private static BlobClientOptions? BuildClientOptions(IConfiguration configuration, ILogger logger)
    {
        var configured = configuration["ArtifactsStorage:ServiceVersion"];
        if (string.IsNullOrWhiteSpace(configured)) return null;

        if (Enum.TryParse<BlobClientOptions.ServiceVersion>(configured, ignoreCase: true, out var version))
        {
            logger.LogInformation("Blob storage pinned to service version {Version}.", version);
            return new BlobClientOptions(version);
        }

        logger.LogWarning(
            "ArtifactsStorage:ServiceVersion '{Value}' is not a known BlobClientOptions.ServiceVersion. " +
            "Falling back to the SDK default.", configured);
        return null;
    }

    private static void CreateContainerWithRetry(BlobContainerClient client)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                client.CreateIfNotExists();
                return;
            }
            catch when (attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    public async Task<string?> UploadBinaryFileAsync(
        string userId, string jobId, string fileName, byte[] data, string contentType, CancellationToken ct = default)
    {
        if (_containerClient == null) return null;

        try
        {
            var blobPath = BuildBlobPath(userId, jobId, fileName);
            var blobClient = _containerClient.GetBlobClient(blobPath);

            using var stream = new MemoryStream(data);
            await blobClient.UploadAsync(
                stream, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);

            _logger.LogInformation("Uploaded artifact: {BlobPath}", blobPath);
            return blobPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload artifact {FileName} for job {JobId}", fileName, jobId);
            return null;
        }
    }

    public async Task<(Stream Stream, string ContentType)?> DownloadFileAsync(string blobPath, CancellationToken ct = default)
    {
        if (_containerClient == null) return null;

        try
        {
            var blobClient = _containerClient.GetBlobClient(blobPath);
            var exists = await blobClient.ExistsAsync(ct);
            if (!exists.Value) return null;

            var download = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
            var contentType = download.Value.Details.ContentType ?? "application/octet-stream";
            return (download.Value.Content, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download artifact {BlobPath}", blobPath);
            return null;
        }
    }

    public async Task DeleteFileAsync(string blobPath, CancellationToken ct = default)
    {
        if (_containerClient == null) return;

        try
        {
            await _containerClient.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Best-effort: a purge already committed the DB half, and a blob
            // this failed to remove is a leak to clean up later, not a reason
            // to report the purge itself as failed.
            _logger.LogError(ex, "Failed to delete artifact {BlobPath}", blobPath);
        }
    }

    private static string BuildBlobPath(string userId, string jobId, string fileName) => $"{userId}/{jobId}/{fileName}";
}
