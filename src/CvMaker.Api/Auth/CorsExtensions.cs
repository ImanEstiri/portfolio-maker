namespace CvMaker.Api.Auth;

public static class CorsExtensions
{
    public const string PolicyName = "cvmaker";

    /// <summary>
    /// Registers the one CORS policy, in every environment.
    ///
    /// This is not a development convenience. CvMaker.Web is a Blazor <b>WebAssembly</b> app, so
    /// every call it makes is a browser <c>fetch</c> from the web app's origin to the API's —
    /// two different hosts once deployed. Without an <c>Access-Control-Allow-Origin</c> header
    /// the browser blocks all of them and the deployed product is inert while every service
    /// reports healthy, which is the confusing failure this exists to prevent.
    ///
    /// Origins come from <c>Cors:AllowedOrigins</c> (a list) so each environment names its own
    /// in <c>fly.toml</c>. Development falls back to the local web origins when nothing is
    /// configured; no other environment gets a fallback, because guessing origins in production
    /// is how a wildcard ends up shipped.
    ///
    /// <c>AllowAnyHeader</c> covers <c>Authorization</c>, and the methods are the ones the API
    /// serves. Both matter for preflight: a <c>PUT</c>/<c>DELETE</c>, or any request carrying a
    /// bearer token, triggers an <c>OPTIONS</c> first.
    /// </summary>
    public static IServiceCollection AddCvMakerCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        if (origins.Length == 0 && environment.IsDevelopment())
        {
            origins =
            [
                "http://localhost:5080",
                "https://localhost:5081",
                "http://localhost:8081"
            ];
        }

        return services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (origins.Length == 0)
            {
                // No origins configured outside Development. Deliberately a policy that allows
                // nothing rather than one that allows everything: the request fails in the
                // browser either way, and this way it cannot silently become public.
                return;
            }

            policy
                .WithOrigins(origins)
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .AllowAnyHeader()
                // The PDF download reads Content-Disposition for the filename, which is not
                // one of the headers CORS exposes by default.
                .WithExposedHeaders("Content-Disposition");
        }));
    }

    /// <summary>The configured origins, for the startup log.</summary>
    public static string DescribeCorsOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        if (origins.Length > 0)
            return string.Join(", ", origins);

        return environment.IsDevelopment() ? "(development defaults)" : "(none — cross-origin calls will fail)";
    }
}
