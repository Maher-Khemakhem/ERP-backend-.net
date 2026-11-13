using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Ecommerce;
using SupermarketErp.Api.Features.Finance;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pos;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Features.Procurement;
using SupermarketErp.Api.Infrastructure;

LocalEnvironmentFile.Load();
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080");
builder.Services.AddSupermarketErp(builder.Configuration, builder.Environment);

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<SqlServerDataSource>();
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

public partial class Program { }
