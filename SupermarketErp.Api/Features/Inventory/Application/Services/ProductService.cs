using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Inventory;

/// <summary>Product catalog use cases. The product entity enforces its own rules (SKU, unit, VAT, reorder point).</summary>
public sealed class ProductService(IProductRepository repository, IUnitOfWork unitOfWork) : IProductService
{
    public async Task<IReadOnlyList<ProductView>> List(CancellationToken ct) =>
        (await repository.List(ct)).Select(ToView).ToArray();

    public async Task<ProductView?> Get(Guid productId, CancellationToken ct) =>
        await repository.Find(productId, ct) is { } product ? ToView(product) : null;

    public async Task<Guid> Create(CreateProductRequest request, CancellationToken ct)
    {
        var product = ProductEntity.Create(request.Sku, request.Name, request.FamilyId, request.ImageUrl, request.UnitType,
            request.Perishable, request.ReorderPoint, request.VatRate);
        await unitOfWork.InTransaction(async () =>
        {
            if (request.FamilyId is { } familyId && !await repository.FamilyExists(familyId, ct))
                throw new DomainValidationException($"Product family not found: {familyId}");
            repository.Add(product);
        }, ct);
        return product.Id;
    }

    /// <summary>Deleting a product that is still referenced (e.g. by stock) is refused as a conflict.</summary>
    public Task<bool> Delete(Guid productId, CancellationToken ct) =>
        unitOfWork.InTransaction(async () =>
        {
            var product = await repository.Find(productId, ct);
            if (product is null) return false;
            repository.Remove(product);
            return true;
        }, ct);

    private static ProductView ToView(ProductEntity x) => new(
        x.Id, x.Sku, x.Name, x.FamilyId, x.Family?.Name, x.Family?.ImageUrl, x.ImageUrl,
        x.UnitType, x.Perishable, x.ReorderPoint ?? 0, x.VatRate ?? ProductEntity.DefaultVatRate);
}
