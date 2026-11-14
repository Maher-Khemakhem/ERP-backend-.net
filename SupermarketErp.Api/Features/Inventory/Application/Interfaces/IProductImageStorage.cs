namespace SupermarketErp.Api.Features.Inventory;

public interface IProductImageStorage
{
    Task<string> Save(IFormFile? file, CancellationToken ct);
    Task<(byte[] Content, string ContentType)?> Read(string filename, CancellationToken ct);
}
