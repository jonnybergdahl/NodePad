using System.Threading.RateLimiting;
using Bergdahl.NodePad.WebApp;
using Bergdahl.NodePad.WebApp.Auth;
using Bergdahl.NodePad.WebApp.Logging;
using Bergdahl.NodePad.WebApp.Mcp;
using Bergdahl.NodePad.WebApp.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
// Load external settings from nodepad.json to hold user-adjustable options
builder.Configuration.AddJsonFile("nodepad.json", optional: true, reloadOnChange: true);
// The account lives in its own file so it stays out of source control
builder.Configuration.AddJsonFile(CredentialStore.FileName, optional: true, reloadOnChange: true);

builder.Services.AddControllers(options =>
{
    options.InputFormatters.Add(new TextPlainInputFormatter());
});

// File system work on the notes tree, shared by the REST API and the MCP tools
builder.Services.AddScoped<NotesService>();

// Lägg till logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Bind file logger options and register file logger provider via DI (avoid BuildServiceProvider)
builder.Services.Configure<FileLoggerOptions>(builder.Configuration.GetSection("Logging:File"));
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();

// Authentication: a single account stored in nodepad.auth.json, signed in with a cookie
var authFilePath = Path.Combine(builder.Environment.ContentRootPath, CredentialStore.FileName);
builder.Services.AddSingleton(sp => new CredentialStore(sp.GetRequiredService<IConfiguration>(), authFilePath));

// Persist data protection keys so auth cookies survive restarts and IIS app pool recycles
var keysDir = builder.Configuration.GetValue<string>("DataProtectionKeysDirectory") ?? "Keys";
builder.Services.AddDataProtection()
    .SetApplicationName("NodePad")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, keysDir)));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    // API key (Authorization: Bearer / X-Api-Key) for the MCP endpoint
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
    .AddCookie(options =>
    {
        options.Cookie.Name = "NodePad.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login.html";
        options.Events.OnRedirectToLogin = context =>
        {
            // API callers get a 401 instead of an HTML redirect
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            // Reject cookies issued before the last password change (or for a removed account)
            var credentials = context.HttpContext.RequestServices.GetRequiredService<CredentialStore>();
            var stamp = context.Principal?.FindFirst(AuthController.StampClaim)?.Value;
            if (!credentials.IsConfigured || stamp != credentials.Stamp)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

// Everything requires a signed-in user unless explicitly marked [AllowAnonymous]
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy(McpPolicy, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
});

// MCP server (Streamable HTTP) so AI assistants can read and edit notes
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "NodePad",
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        };
        options.ServerInstructions = NotesTools.Instructions;
    })
    .WithHttpTransport()
    .WithTools<NotesTools>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthController.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Lägg till felhantering
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

if (!app.Services.GetRequiredService<CredentialStore>().IsConfigured)
{
    app.Logger.LogWarning("No account configured. Open NodePad in a browser to create one.");
}

// The login page and the assets it uses are served before authentication kicks in
var publicFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/login.html", "/favicon.ico", "/assets/nodepad_logo.png", "/assets/nodepad_icon.png",
};
app.UseWhen(context => publicFiles.Contains(context.Request.Path.Value ?? string.Empty), branch => branch.UseStaticFiles());

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.UseDefaultFiles();
app.UseStaticFiles();

// Serve PagesDirectory under /pages for embedded assets (images placed next to markdown files)
var pagesDir = builder.Configuration.GetValue<string>("PagesDirectory") ?? Path.Combine(Directory.GetCurrentDirectory(), "Pages");
var pagesFullPath = Path.GetFullPath(pagesDir);
if (!Directory.Exists(pagesFullPath))
{
    Directory.CreateDirectory(pagesFullPath);
}
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pagesFullPath),
    RequestPath = "/pages",
});

app.MapControllers();
app.MapMcp("/mcp").RequireAuthorization(McpPolicy);

app.Run();

// Exposed for integration tests (WebApplicationFactory)
public partial class Program
{
    private const string McpPolicy = "Mcp";
}
