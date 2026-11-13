using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Ecommerce;
using SupermarketErp.Api.Features.Finance;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pos;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Features.Procurement;

namespace SupermarketErp.Api.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSupermarketErp(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.Converters.Add(new JavaInstantJsonConverter());
        });

        services.AddSingleton(new SqlServerDataSource(DatabaseConnectionString.Create(configuration)));
        services.AddDbContext<AuthDbContext>((serviceProvider, options) =>
            options.UseSqlServer(serviceProvider.GetRequiredService<SqlServerDataSource>().ConnectionString));
        services.AddScoped<AuthService>();
        services.AddScoped<ProductRepository>();
        services.AddScoped<InventoryRepository>();
        services.AddSingleton<ProductImageStorage>();
        services.AddScoped<PricingRepository>();
        services.AddScoped<ProcurementRepository>();
        services.AddScoped<PosRepository>();
        services.AddScoped<FinanceRepository>();
        services.AddScoped<StorefrontCatalog>();
        services.AddScoped<StorefrontOrderService>();
        services.AddScoped<StripeCheckoutService>();
        services.AddHttpClient("stripe", client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<ScannerPairingService>();
        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<OnlineOrderExpiryWorker>();

        AddAuthentication(services, configuration);
        AddAuthorization(services);
        services.AddSingleton<LoginRateLimiter>();
        services.AddCors(options => options.AddPolicy("java-compatible", policy => policy
            .SetIsOriginAllowed(IsAllowedDevelopmentOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
        services.AddOpenApi();
        services.AddProblemDetails();

        return services;
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        var secret = configuration["JWT_SECRET"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException("JWT_SECRET must be set and contain at least 32 bytes.");
        }

        var issuer = configuration["JWT_ISSUER"] ?? "supermarket-erp";
        var audience = configuration["JWT_AUDIENCE"] ?? "supermarket-web";
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    ValidateLifetime = true,
                    RoleClaimType = "roles",
                    NameClaimType = "sub",
                    ClockSkew = TimeSpan.FromSeconds(60)
                };
            });
    }

    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddAuthorization(options =>
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
    }

    private static bool IsAllowedDevelopmentOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.IsDefaultPort)
        {
            return false;
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 127
                || bytes[0] == 10
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 172 && bytes[1] == 16)
            : IPAddress.IsLoopback(address);
    }
}
