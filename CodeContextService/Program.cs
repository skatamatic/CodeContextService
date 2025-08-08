using CodeContextService.API;
using CodeContextService.Components;
using CodeContextService.Model;
using CodeContextService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Build.Locator;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using RoslynTools.Analyzer;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true) // <- added
    .AddEnvironmentVariables();

// 1) Bind your JWT settings and in-memory users from configuration
var jwtSettings = builder.Configuration
    .GetSection("JwtSettings")
    .Get<JwtSettings>()!;
var users = builder.Configuration
    .GetSection("UserCredentials")
    .Get<List<UserCredential>>()!;

// 2) Register settings, users, AuthService & AuthStateProvider for DI
builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton(users);
// register the concrete JwtAuthenticationStateProvider so it can be injected
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider, JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddAuthorizationCore();

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

// 4) Your existing Razor + Services
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddScoped<AzureDevOpsIntegrationService>();
builder.Services.AddScoped<SourceControlIntegrationService>();
builder.Services.AddScoped<GitHubIntegrationService>();
builder.Services.AddScoped<PRAnalyzerService>();
builder.Services.AddScoped(sp =>
    new DefinitionFinderServiceV2(msg => Console.WriteLine($"[RF] {msg}"))
);

var msbuildPath = builder.Configuration["LocalSettings:MSBuildPath"];
if (!string.IsNullOrWhiteSpace(msbuildPath))
{
    MSBuildLocator.RegisterMSBuildPath(msbuildPath);
}
else
{
    MSBuildLocator.RegisterDefaults();
}


// 5) Swagger for testing
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // 1. define the scheme
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Enter the JWT (no leading “Bearer ”).",
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
.AllowAnonymous();

// 8) UI registration with Anonymous allowed (so login page loads)
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

// 9) Existing API endpoints (protected by fallback)
app.MapPrAnalyzerEndpoints();

app.Run();
