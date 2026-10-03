namespace CvMaker.Api.Services.Storage;

/// <summary>
/// Stores and retrieves generated CV PDFs in Azure Blob Storage rather than in
/// the database: CVs are retained artifacts and do not belong in every database
/// backup. Blobs are stored under <c>{userId}/{jobId}/{fileName}</c>.
/// </summary>
public interface IArtifactStorageService
{
    /// <summary>
    /// Uploads a binary file (the rendered PDF) and returns the blob path, or
    /// <see langword="null"/> when storage is not configured or the upload failed.
    /// </summary>
    Task<string?> UploadBinaryFileAsync(
        string userId, string jobId, string fileName, byte[] data, string contentType, CancellationToken ct = default);

    /// <summary>Downloads a blob as a stream. Returns null when not configured or the blob is missing.</summary>
    Task<(Stream Stream, string ContentType)?> DownloadFileAsync(string blobPath, CancellationToken ct = default);

    /// <summary>
    /// Deletes a blob as part of a GDPR purge (MeEndpoints.PurgeAsync). A blob
    /// that is already gone satisfies the caller just as well as one this call
    /// removed, so that case is not an error.
    /// </summary>
    Task DeleteFileAsync(string blobPath, CancellationToken ct = default);

    /// <summary>True when storage is backed by a real (or emulated) Azure Blob Storage account.</summary>
    bool IsConfigured { get; }
}
