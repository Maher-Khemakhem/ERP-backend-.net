using SupermarketErp.Api.Features.Auth;
using Microsoft.Data.SqlClient;

namespace SupermarketErp.Api.Features.Inventory;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/inventory/products")
            .WithTags("Inventory products");

        products.MapGet("", async (ProductRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.List(ct)))
            .RequireAuthorization(Policies.StaffRead);

        products.MapGet("/{productId:guid}", async (Guid productId, ProductRepository repository, CancellationToken ct) =>
            await repository.Get(productId, ct) is { } product ? Results.Ok(product) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);

        products.MapPost("", async (CreateProductRequest request, ProductRepository repository, CancellationToken ct) =>
        {
            var productId = await repository.Create(request, ct);
            return Results.Ok(new { productId });
        }).RequireAuthorization(Policies.InventoryWrite);

        products.MapDelete("/{productId:guid}", async (Guid productId, ProductRepository repository, CancellationToken ct) =>
        {
            try
            {
                return await repository.Delete(productId, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                return Results.Conflict();
            }
        })
            .RequireAuthorization(Policies.InventoryWrite);

        return endpoints;
    }
}
