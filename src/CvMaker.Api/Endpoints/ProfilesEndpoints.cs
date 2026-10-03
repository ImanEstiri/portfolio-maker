using CvMaker.Api.Data;
using CvMaker.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Endpoints;

public static class ProfilesEndpoints
{
    public static RouteGroupBuilder MapProfilesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profiles").WithTags("Profiles");

        group.MapPost("/", CreateAsync).WithName("CreateProfile");
        group.MapGet("/", ListAsync).WithName("ListProfiles");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetProfile");
        group.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateProfile");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteProfile");

        return group;
    }

    private static async Task<IResult> CreateAsync(
        UpsertProfileRequest request, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        if (Validate(request) is { } problem) return problem;

        var now = DateTime.UtcNow;
        var entity = new ProfileEntity
        {
            Id = Guid.NewGuid(),
            UserId = http.UserId(),
            CreatedAt = now,
            UpdatedAt = now
        };

        Apply(entity, request);
        db.Profiles.Add(entity);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/profiles/{entity.Id}", entity.ToDto());
    }

    private static async Task<IResult> ListAsync(AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = http.UserId();
        var profiles = await db.Profiles
            .Include(p => p.Facts)
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(profiles.Select(p => p.ToDto()));
    }

    private static async Task<IResult> GetAsync(
        Guid id, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var entity = await db.Profiles
            .Include(p => p.Facts)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        // Ownership is checked as part of the lookup result, and a profile
        // belonging to someone else is reported as absent rather than
        // forbidden — otherwise the 403 confirms the id exists.
        return entity is null || entity.UserId != http.UserId()
            ? Results.NotFound()
            : Results.Ok(entity.ToDto());
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpsertProfileRequest request, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        if (Validate(request) is { } problem) return problem;

        var entity = await db.Profiles
            .Include(p => p.Facts)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (entity is null || entity.UserId != http.UserId()) return Results.NotFound();

        db.ProfileFacts.RemoveRange(entity.Facts);
        entity.Facts.Clear();

        Apply(entity, request);

        // Every fact now in the collection is new, and Apply gives each a client-assigned id.
        // Left to change detection, EF treats a child that arrives with a key already set as an
        // existing row and issues an UPDATE for it; the row is not there, so the save fails with
        // a concurrency exception and the caller sees a 500. Saying so explicitly makes it an
        // INSERT.
        foreach (var fact in entity.Facts) db.Entry(fact).State = EntityState.Added;

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(entity.ToDto());
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var entity = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null || entity.UserId != http.UserId()) return Results.NotFound();

        db.Profiles.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private const int MaxFacts = 500;

    private static IResult? Validate(UpsertProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["fullName"] = ["A name is required."]
            });

        if (request.Facts is { Count: > MaxFacts })
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["facts"] = [$"A profile may hold at most {MaxFacts} facts."]
            });

        // Fact ids are what generated content cites, so ambiguity here would
        // make provenance meaningless. The database enforces this too; catching
        // it first turns a 500 into a useful message.
        //
        // Only ids the client actually supplied are checked. Blank ones are not duplicates of
        // each other — they are the documented "let the server name it" case, filled in by
        // Apply — and grouping them together rejected any profile posted by a plain form with
        // more than one fact, which is the ordinary way to create one.
        var duplicate = request.Facts?
            .Where(f => !string.IsNullOrWhiteSpace(f.FactId))
            .GroupBy(f => f.FactId, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["facts"] = [$"Duplicate factId '{duplicate.Key}'."]
            });

        return null;
    }

    private static void Apply(ProfileEntity entity, UpsertProfileRequest request)
    {
        entity.FullName = request.FullName;
        entity.Headline = request.Headline;
        entity.Email = request.Email;
        entity.Phone = request.Phone;
        entity.Location = request.Location;
        entity.Links = request.Links is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(request.Links);

        var facts = request.Facts ?? Array.Empty<ProfileFactDto>();

        // Generated ids have to avoid the supplied ones, not just each other. Numbering by
        // position meant a request supplying "f1" for the first fact and leaving the second
        // blank generated "f1" for it as well — a unique-index violation on a request that is
        // entirely valid, and a provenance collision if it had landed.
        var taken = facts
            .Where(f => !string.IsNullOrWhiteSpace(f.FactId))
            .Select(f => f.FactId!)
            .ToHashSet(StringComparer.Ordinal);

        var next = 0;
        string NextGeneratedId()
        {
            string candidate;
            do
            {
                candidate = $"f{next++}";
            }
            while (!taken.Add(candidate));

            return candidate;
        }

        foreach (var fact in facts)
        {
            entity.Facts.Add(new ProfileFactEntity
            {
                Id = Guid.NewGuid(),
                ProfileId = entity.Id,
                // Generated when the client does not supply one, so provenance
                // works for a plain form post that knows nothing about facts.
                FactId = string.IsNullOrWhiteSpace(fact.FactId) ? NextGeneratedId() : fact.FactId,
                Kind = fact.Kind,
                Text = fact.Text,
                Organization = fact.Organization,
                Role = fact.Role,
                Location = fact.Location,
                StartDate = fact.StartDate,
                EndDate = fact.EndDate
            });
        }
    }
}
