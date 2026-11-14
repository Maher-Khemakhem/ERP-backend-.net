namespace SupermarketErp.Api.Features.Inventory;

public sealed class ProductService(IProductRepository repository) : IProductService
{
    public Task<IReadOnlyList<ProductView>> List(CancellationToken ct) =>
        repository.List(ct);

    public Task<ProductView?> Get(Guid productId, CancellationToken ct) =>
        repository.Get(productId, ct);

    public Task<Guid> Create(CreateProductRequest request, CancellationToken ct) =>
        repository.Create(request, ct);

    public Task<bool> Delete(Guid productId, CancellationToken ct) =>
        repository.Delete(productId, ct);
}
