using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Procurement;

public static class ProcurementEndpoints
{
    public static IEndpointRouteBuilder MapProcurementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var suppliers = endpoints.MapGroup("/api/procurement/suppliers").WithTags("Suppliers");
        suppliers.MapGet("", async (IProcurementService service, CancellationToken ct) => Results.Ok(await service.ListSuppliers(ct)))
            .RequireAuthorization(Policies.StaffRead);
        suppliers.MapGet("/{id:guid}", async (Guid id, IProcurementService service, CancellationToken ct) =>
            await service.GetSupplier(id, ct) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(Policies.StaffRead);
        suppliers.MapPost("", async (SupplierRequest request, IProcurementService service, CancellationToken ct) =>
            Results.Ok(new { id = await service.CreateSupplier(request, ct) }))
            .RequireAuthorization(Policies.InventoryWrite);
        suppliers.MapPut("/{id:guid}", async (Guid id, SupplierRequest request, IProcurementService service, CancellationToken ct) =>
            await service.UpdateSupplier(id, request, ct) ? Results.Ok(new { id }) : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);
        suppliers.MapDelete("/{id:guid}", async (Guid id, IProcurementService service, CancellationToken ct) =>
            await service.DeleteSupplier(id, ct) ? Results.Ok() : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);

        var orders = endpoints.MapGroup("/api/procurement/orders").WithTags("Purchase orders");
        orders.MapGet("", async (IProcurementService service, CancellationToken ct) => Results.Ok(await service.ListOrders(ct)))
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapGet("/{id:guid}", async (Guid id, IProcurementService service, CancellationToken ct) =>
            await service.GetOrder(id, ct) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("", async (CreateOrderRequest request, IProcurementService service, CancellationToken ct) =>
            Results.Ok(new { id = await service.CreateOrder(request, ct) }))
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("/{id:guid}/place", async (Guid id, IProcurementService service, CancellationToken ct) =>
        {
            await service.PlaceOrder(id, ct);
            return Results.Ok(new { id });
        })
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("/{id:guid}/cancel", async (Guid id, IProcurementService service, CancellationToken ct) =>
        {
            await service.CancelOrder(id, ct);
            return Results.Ok(new { id });
        })
            .RequireAuthorization(Policies.InventoryWrite);
        orders.MapPost("/{id:guid}/receive", async (Guid id, ReceiveOrderRequest request, IProcurementService service, CancellationToken ct) =>
        {
            await service.ReceiveOrder(id, request, ct);
            return Results.Ok(new { id });
        }).RequireAuthorization(Policies.InventoryWrite);

        var alerts = endpoints.MapGroup("/api/procurement/alerts").WithTags("Low stock alerts");
        alerts.MapGet("", async (IProcurementService service, CancellationToken ct) => Results.Ok(await service.ListAlerts(ct)))
            .RequireAuthorization(Policies.StaffRead);
        alerts.MapPost("/{id:guid}/acknowledge", async (Guid id, IProcurementService service, CancellationToken ct) =>
        {
            await service.AcknowledgeAlert(id, ct);
            return Results.Ok(new { id });
        })
            .RequireAuthorization(Policies.InventoryWrite);
        return endpoints;
    }
}
