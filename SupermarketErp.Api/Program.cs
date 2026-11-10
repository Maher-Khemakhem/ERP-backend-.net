using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Features.Procurement;
using SupermarketErp.Api.Features.Pos;
using SupermarketErp.Api.Features.Finance;
using SupermarketErp.Api.Features.Ecommerce;
using SupermarketErp.Api.Infrastructure;

LocalEnvironmentFile.Load();
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.Converters.Add(new JavaInstantJsonConverter());
});

var connectionString = DatabaseConnectionString.Create(builder.Configuration);
var dataSource = new SqlServerDataSource(connectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<InventoryRepository>();
builder.Services.AddSingleton<ProductImageStorage>();
builder.Services.AddScoped<PricingRepository>();
builder.Services.AddScoped<ProcurementRepository>();
builder.Services.AddScoped<PosRepository>();
builder.Services.AddScoped<FinanceRepository>();
builder.Services.AddScoped<StorefrontCatalog>();
builder.Services.AddScoped<StorefrontOrderService>();
builder.Services.AddScoped<StripeCheckoutService>();
builder.Services.AddHttpClient("stripe", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<ScannerPairingService>();
builder.Services.AddHostedService<OutboxProcessor>();
builder.Services.AddHostedService<OnlineOrderExpiryWorker>();

var jwtSecret = builder.Configuration["JWT_SECRET"];
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
{
    throw new InvalidOperationException("JWT_SECRET must be set and contain at least 32 bytes.");
}

var jwtIssuer = builder.Configuration["JWT_ISSUER"] ?? "supermarket-erp";
var jwtAudience = builder.Configuration["JWT_AUDIENCE"] ?? "supermarket-web";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            RoleClaimType = "roles",
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromSeconds(60)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy(Policies.StaffRead, policy => policy.RequireRole(
        Roles.Admin, Roles.Manager, Roles.Cashier, Roles.InventoryManager, Roles.Accountant));
    options.AddPolicy(Policies.InventoryWrite, policy => policy.RequireRole(
        Roles.Admin, Roles.Manager, Roles.InventoryManager));
    options.AddPolicy(Policies.Admin, policy => policy.RequireRole(Roles.Admin));
});

builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddCors(options => options.AddPolicy("java-compatible", policy => policy
    .SetIsOriginAllowed(IsAllowedDevelopmentOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.AddOpenApi();

var app = builder.Build();
await IdentityDatabaseInitializer.EnsureCreated(dataSource);
app.UseMiddleware<ApiExceptionMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});
app.UseCors("java-compatible");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapGet("/health/live", () => Results.Ok(new { status = "UP" })).AllowAnonymous();
app.MapGet("/health/ready", async (SqlServerDataSource db, CancellationToken ct) =>
{
    await using var connection = await db.OpenConnectionAsync(ct);
    return Results.Ok(new { status = "UP", database = "UP" });
}).AllowAnonymous();

app.MapAuthEndpoints();
app.MapProductEndpoints();
app.MapInventoryEndpoints();
app.MapProductImages();
app.MapPricingEndpoints();
app.MapProcurementEndpoints();
app.MapPosEndpoints();
app.MapFinanceEndpoints();
app.MapStorefrontCatalog();
app.MapStorefrontOrderEndpoints();
app.MapStripeCheckout();

app.Run();

static bool IsAllowedDevelopmentOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        || uri.Scheme is not ("http" or "https")
        || uri.IsDefaultPort)
    {
        return false;
    }

    var host = uri.Host;
    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
    if (!System.Net.IPAddress.TryParse(host, out var address)) return false;
    var bytes = address.GetAddressBytes();
    if (bytes.Length == 4)
    {
        return bytes[0] == 127
            || bytes[0] == 10
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 172 && bytes[1] == 16);
    }

    return System.Net.IPAddress.IsLoopback(address);
}

public partial class Program { }
