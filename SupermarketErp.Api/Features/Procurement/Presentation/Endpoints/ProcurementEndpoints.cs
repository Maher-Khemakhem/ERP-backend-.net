using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Procurement;

public static class ProcurementEndpoints
{
    public static IEndpointRouteBuilder MapProcurementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var suppliers = endpoints.MapGroup("/api/procurement/suppliers").WithTags("Suppliers");
        suppliers.MapGet("", async (ProcurementRepository repo, CancellationToken ct) => Results.Ok(await repo.ListSuppliers(ct)))
            .RequireAuthorization(Policies.StaffRead);
        suppliers.MapGet("/{id:guid}", async (Guid id, ProcurementRepository repo, CancellationToken ct) =>
            await repo.GetSupplier(id, ct) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);
        suppliers.MapPost("", async (SupplierRequest request, ProcurementRepository repo, CancellationToken ct) =>
            Results.Ok(new { id = await repo.CreateSupplier(request, ct) }))
            .RequireAuthorization(Policies.InventoryWrite);
        suppliers.MapPut("/{id:guid}", async (Guid id, SupplierRequest request, ProcurementRepository repo, CancellationToken ct) =>
            await repo.UpdateSupplier(id, request, ct) ? Results.Ok(new { id }) : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);
        suppliers.MapDelete("/{id:guid}", async (Guid id, ProcurementRepository repo, CancellationToken ct) =>
            await repo.DeleteSupplier(id, ct) ? Results.Ok() : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);

        var orders = endpoints.MapGroup("/api/procurement/orders").WithTags("Purchase orders");
        orders.MapGet("", async (ProcurementRepository repo, CancellationToken ct) => Results.Ok(await repo.ListOrders(ct)))
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapGet("/{id:guid}", async (Guid id, ProcurementRepository repo, CancellationToken ct) =>
            await repo.GetOrder(id, ct) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("", async (CreateOrderRequest request, ProcurementRepository repo, CancellationToken ct) =>
            Results.Ok(new { id = await repo.CreateOrder(request, ct) }))
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("/{id:guid}/place", async (Guid id, ProcurementRepository repo, CancellationToken ct) =>
        {
            await repo.PlaceOrder(id, ct);
            return Results.Ok(new { id });
        })
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("/{id:guid}/cancel", async (Guid id, ProcurementRepository repo, CancellationToken ct) =>
        {
            await repo.CancelOrder(id, ct);
            return Results.Ok(new { id });
        })
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("/{id:guid}/receive", async (Guid id, ReceiveOrderRequest request, ProcurementRepository repo, CancellationToken ct) =>
        {
            await repo.ReceiveOrder(id, request, ct);
            return Results.Ok(new { id });
        }).RequireAuthorization(Policies.InventoryWrite);

        var alerts = endpoints.MapGroup("/api/procurement/alerts").WithTags("Low stock alerts");
        alerts.MapGet("", async (ProcurementRepository repo, CancellationToken ct) => Results.Ok(await repo.ListAlerts(ct)))
            .RequireAuthorization(Policies.StaffRead);
        alerts.MapPost("/{id:guid}/acknowledge", async (Guid id, ProcurementRepository repo, CancellationToken ct) =>
        {
            await repo.AcknowledgeAlert(id, ct);
            return Results.Ok(new { id });
        })
            .RequireAuthorization(Policies.InventoryWrite);
        return endpoints;
    }
}
