using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using CodeContextService.API;
using CodeContextService.Components;
using CodeContextService.Model;
using CodeContextService.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Build.Locator;
using Microsoft.IdentityModel.Tokens;
using RoslynTools.Analyzer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);

// 1) Bind JWT, users, and repo clone options from configuration (env vars override appsettings)
var jwtSettings = builder.Configuration
    .GetSection("JwtSettings")
    .Get<JwtSettings>()!;
var users = builder.Configuration
    .GetSection("UserCredentials")
    .Get<List<UserCredential>>()!;

if (builder.Environment.IsProduction() && (jwtSettings.Key.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(jwtSettings.Key)))
    throw new InvalidOperationException("In Production, set JwtSettings:Key via environment (e.g. JwtSettings__Key) or Secret Manager. Do not use placeholder from appsettings.json.");

if (builder.Environment.IsProduction() && (users.Count == 0 || users.Any(u => string.IsNullOrWhiteSpace(u.Password) || u.Password.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase))))
    throw new InvalidOperationException("In Production, set at least one user via environment (e.g. UserCredentials__0__Username, UserCredentials__0__Password, UserCredentials__0__Role) or Secret Manager. Do not use placeholder passwords from appsettings.json.");

builder.Services.Configure<RepoCloneOptions>(
    builder.Configuration.GetSection(RepoCloneOptions.SectionName));

builder.Services.AddHttpContextAccessor();

// 2) Register settings, users, AuthService & AuthStateProvider for DI
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton(users);
// register the concrete JwtAuthenticationStateProvider so it can be injected
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// 3) Add Authentication & Authorization
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        var keyBytes = Encoding.UTF8.GetBytes(jwtSettings.Key);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization(opts =>
{
    // require auth on all API endpoints by default
    opts.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            JwtBearerDefaults.AuthenticationScheme
        )
        .RequireAuthenticatedUser()
        .Build();
});

// Rate limiting: protect login from brute force, analyze from spam/DoS
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        await context.HttpContext.Response.WriteAsync("Too many requests. Try again later.", context.HttpContext.RequestAborted);
    };

    // Login: strict per-IP to slow brute force (5 attempts per minute per IP)
    options.AddPolicy("login", context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString()
            ?? context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true
        });
    });

    // Analyze: per-user (expensive clone + Roslyn), 10 per minute per authenticated user
    options.AddPolicy("analyze", context =>
    {
        var key = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true
        });
    });
});

// 4) Razor + Services; show detailed circuit errors in Development or when opted in (e.g. CircuitOptions__DetailedErrors=true in Docker)
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
var circuitDetailedErrors = builder.Environment.IsDevelopment()
    || string.Equals(builder.Configuration["CircuitOptions:DetailedErrors"], "true", StringComparison.OrdinalIgnoreCase);
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o => o.DetailedErrors = circuitDetailedErrors);
// Allow larger JS interop payloads (e.g. zip-upload analysis results); default SignalR limit is 32 KB
builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024); // 4 MB

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("github-no-redirect")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        // Avoid opaque redirect loops ("too many redirects or authentication replays").
        // We handle redirect responses explicitly in GitHubIntegrationService.
        AllowAutoRedirect = false
    });
builder.Services.AddScoped<GitHubIntegrationService>();
builder.Services.AddScoped<PRAnalyzerService>();
builder.Services.AddScoped<UploadAnalyzerService>();
builder.Services.AddScoped(sp =>
    new DefinitionFinderService(msg => Console.WriteLine($"[RF] {msg}"))
);
MSBuildLocator.RegisterDefaults();

// 5) Swagger for testing
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // 1. define the scheme
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter the JWT (no leading �Bearer �).",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    // 2. require the scheme for all operations (so the lock icon appears)
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme {
                Reference = new OpenApiReference {
                    Type = ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()  // no scopes for JWT bearer
        }
    });
});

var app = builder.Build();

// Allow larger request body for zip upload endpoint only (default Kestrel limit is 30 MB)
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/api/analyze-upload", StringComparison.OrdinalIgnoreCase),
    subApp =>
    {
        subApp.Use(async (context, next) =>
        {
            var feature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature != null)
                feature.MaxRequestBodySize = 524_288_000; // 500 MB
            await next(context);
        });
    });

// 6) Middleware pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
else
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Skip HTTPS redirect when running in Docker/container (no HTTPS listener); avoids endless load
if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// **ORDER MATTERS**: auth must come before any endpoint mapping
app.UseAuthentication();
app.UseAuthorization();

// 7) Public login endpoint to issue JWTs
app.MapPost("/api/auth/login", (LoginRequest req) =>
{
    var user = users.SingleOrDefault(u =>
        u.Username.Equals(req.Username, StringComparison.OrdinalIgnoreCase)
        && u.Password == req.Password
    );
    if (user is null)
        return Results.Unauthorized();

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub,    user.Username),
        new Claim(ClaimTypes.Name,                user.Username),
        new Claim(ClaimTypes.Role,                user.Role)
    };

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key));
    var credsSig = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var expiresAt = DateTime.UtcNow.AddMinutes(jwtSettings.ExpiresMinutes);

    var token = new JwtSecurityToken(
        issuer: jwtSettings.Issuer,
        audience: jwtSettings.Audience,
        claims: claims,
        expires: expiresAt,
        signingCredentials: credsSig
    );

    return Results.Ok(new
    {
        token = new JwtSecurityTokenHandler().WriteToken(token),
        expires = expiresAt
    });
})
.RequireRateLimiting("login")
.AllowAnonymous();

// 8) UI registration with Anonymous allowed (so login page loads)
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

// 9) Existing API endpoints (protected by fallback)
app.MapPrAnalyzerEndpoints();
app.MapUploadAnalyzerEndpoints();

// When in Docker, localhost from the host often hangs (IPv6). Log the URL that works.
if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
{
    app.Lifetime.ApplicationStarted.Register(() =>
        Console.WriteLine("[CodeContextService] When opening in a browser from the host, use http://127.0.0.1:8080 (localhost may hang due to IPv6)."));
}

app.Run();
