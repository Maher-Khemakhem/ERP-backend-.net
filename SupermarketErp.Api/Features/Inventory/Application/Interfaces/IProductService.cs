namespace SupermarketErp.Api.Features.Inventory;

public interface IProductService
{
    Task<IReadOnlyList<ProductView>> List(CancellationToken ct);
    Task<ProductView?> Get(Guid productId, CancellationToken ct);
    Task<Guid> Create(CreateProductRequest request, CancellationToken ct);
    Task<bool> Delete(Guid productId, CancellationToken ct);
}
