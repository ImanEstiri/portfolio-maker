using System.Net.Http.Json;
using CvMaker.Shared;

namespace CvMaker.Web.Services;

/// <summary>
/// Typed client for CvMaker.Api. Mirrors BlackHoleSim.Web's RenderApiClient:
/// the UI never constructs a URL or reads a status code.
/// </summary>
/// <summary>The monthly allowance is spent. Distinct from a transport failure so the UI can explain rather than apologise.</summary>
public sealed class QuotaExhaustedException() : Exception("Quota exhausted.");

public sealed class CvMakerApiClient(HttpClient http)
{
    public HttpClient Http => http;

    public async Task<IReadOnlyList<TemplateDto>> GetTemplatesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<TemplateDto>>("/api/templates", ct) ?? [];

    public async Task<IReadOnlyList<ProfileDto>> GetProfilesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ProfileDto>>("/api/profiles", ct) ?? [];

    public async Task<ProfileDto?> GetProfileAsync(Guid id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ProfileDto>($"/api/profiles/{id}", ct);

    public async Task<ProfileDto?> CreateProfileAsync(UpsertProfileRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/api/profiles", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProfileDto>(ct);
    }

    public async Task<ProfileDto?> UpdateProfileAsync(Guid id, UpsertProfileRequest request, CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"/api/profiles/{id}", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProfileDto>(ct);
    }

    public async Task<JobDto?> CreateDocumentAsync(CreateDocumentRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("/api/documents", request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("You have started too many generations. Wait a minute and try again.");

        if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            throw new QuotaExhaustedException();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JobDto>(ct);
    }

    public async Task<JobDto?> GetJobAsync(Guid id, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<JobDto>($"/api/jobs/{id}", ct);

    public async Task<IReadOnlyList<JobDto>> GetJobsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<JobDto>>("/api/jobs", ct) ?? [];

    public async Task CancelJobAsync(Guid id, CancellationToken ct = default) =>
        await http.DeleteAsync($"/api/jobs/{id}", ct);

    /// <summary>
    /// Fetches the PDF bytes through the authenticated client.
    ///
    /// Not an &lt;a href&gt; to the endpoint: a plain link is a browser
    /// navigation, which carries cookies but not the Authorization header, so
    /// it would 401 the moment auth is switched on. A short-lived signed blob
    /// URL would avoid streaming the PDF through the API and is not built;
    /// fetching the bytes and handing them to the page is correct and costs one
    /// extra copy.
    /// </summary>
    public async Task<byte[]?> DownloadPdfAsync(Guid jobId, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"/api/jobs/{jobId}/pdf", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadAsByteArrayAsync(ct)
            : null;
    }

    /// <summary>
    /// Polls a job to completion.
    ///
    /// Polling rather than SSE or websockets: a tailored generation is up to
    /// four model calls and a LaTeX compile, so the update rate is seconds, and
    /// a poll survives the scale-to-zero machines and proxies in front of it
    /// without a reconnection story.
    /// </summary>
    public async Task<JobDto?> PollUntilDoneAsync(
        Guid jobId, Action<JobDto> onUpdate, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var job = await GetJobAsync(jobId, ct);
            if (job is null) return null;

            onUpdate(job);

            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                return job;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return null;
    }
}
