public sealed record SupplierRequest(string Name, string? ContactEmail, string? Phone, string? Address);
public sealed record SupplierView(Guid Id, string Name, string? ContactEmail, string? Phone, string? Address);
public sealed record OrderLineRequest(Guid ProductId, decimal QuantityValue, string UnitType, decimal UnitCost);
public sealed record CreateOrderRequest(Guid SupplierId, IReadOnlyList<OrderLineRequest> Lines);
public sealed record ReceiveLineRequest(Guid PurchaseOrderLineId, decimal QuantityValue, string UnitType,
    string? LotNumber, DateOnly? ExpiryDate, DateOnly? ReceivedDate, string? SupplierReference);
public sealed record ReceiveOrderRequest(IReadOnlyList<ReceiveLineRequest> Lines);
public sealed record OrderLineView(Guid Id, Guid ProductId, string Quantity, string UnitType, string UnitCost);
public sealed record OrderView(Guid Id, Guid SupplierId, string? SupplierName, string Status,
    IReadOnlyList<OrderLineView> Lines, string TotalCost, string? CreatedAt, string? PlacedAt, string? ReceivedAt);
public sealed record AlertView(Guid Id, Guid ProductId, string ProductName, double CurrentStock,
    int ReorderPoint, string CreatedAt, bool Acknowledged);
