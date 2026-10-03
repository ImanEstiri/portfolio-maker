using CvMaker.Web;
using CvMaker.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base addresses come from wwwroot/appsettings.json so the same built assets
// can be pointed at a different environment without a rebuild.
var apiBase = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
var authBase = builder.Configuration["AuthBaseUrl"] ?? builder.HostEnvironment.BaseAddress;

// Off unless configured — see DevUser for what it does and why it grants nothing by itself.
builder.Services.AddSingleton(new DevUser(builder.Configuration["DevUser"]));

builder.Services.AddSingleton<TokenStore>();
builder.Services.AddTransient<AuthMessageHandler>();

builder.Services.AddHttpClient<CvMakerApiClient>(c => c.BaseAddress = new Uri(apiBase))
       .AddHttpMessageHandler<AuthMessageHandler>();

// Deliberately without AuthMessageHandler: this client is what the handler calls to refresh,
// and attaching it would make a failed refresh retry itself.
builder.Services.AddHttpClient<AuthClient>(c => c.BaseAddress = new Uri(authBase));
builder.Services.AddHttpClient<ConsentVersions>(c => c.BaseAddress = new Uri(authBase));

var host = builder.Build();

// Restore the session before the first render rather than after it.
//
// Blazor WASM reboots the whole runtime on navigation-to-self, so a page refresh, a deep link
// or a browser restore all start from an empty TokenStore. Doing this after RunAsync would
// show every returning user a signed-out UI first and then swap it out underneath them.
await host.Services.GetRequiredService<AuthClient>().RestoreSessionAsync();

await host.RunAsync();
