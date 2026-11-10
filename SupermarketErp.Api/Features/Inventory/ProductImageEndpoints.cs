using Microsoft.AspNetCore.StaticFiles;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Inventory;

public sealed class ProductImageStorage(IConfiguration configuration)
{
    private readonly string _directory = Path.GetFullPath(configuration["APP_UPLOAD_DIR"] ?? "uploads/products");

    public async Task<string> Save(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || string.IsNullOrWhiteSpace(file.ContentType)
            || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An image file is required.");
        if (file.Length > 5 * 1024 * 1024) throw new ArgumentException("Image cannot exceed 5 MB.");
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (extension.Length > 12) extension = "";
        var filename = Guid.NewGuid().ToString() + (string.IsNullOrEmpty(extension) ? ".img" : extension.ToLowerInvariant());
        Directory.CreateDirectory(_directory);
        var fullPath = Path.Combine(_directory, filename);
        await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(stream, ct);
        return "/uploads/products/" + filename;
    }

    public async Task<(byte[] Content, string ContentType)?> Read(string filename, CancellationToken ct)
    {
        var safeName = Path.GetFileName(filename);
        var fullPath = Path.GetFullPath(Path.Combine(_directory, safeName));
        if (!fullPath.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)) return null;
        var provider = new FileExtensionContentTypeProvider();
        var contentType = Path.GetExtension(safeName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => provider.TryGetContentType(safeName, out var resolved) && resolved.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? resolved : "application/octet-stream"
        };
        return (await File.ReadAllBytesAsync(fullPath, ct), contentType);
    }
}

public static class ProductImageEndpoints
{
    public static IEndpointRouteBuilder MapProductImages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/product-images", async (IFormFile file, ProductImageStorage storage, CancellationToken ct) =>
            Results.Ok(new { url = await storage.Save(file, ct) }))
            .RequireAuthorization(Policies.InventoryWrite)
            .DisableAntiforgery();

        endpoints.MapGet("/uploads/products/{filename}", async (string filename, ProductImageStorage storage, CancellationToken ct) =>
        {
            var file = await storage.Read(filename, ct);
            return file is null ? Results.NotFound() : Results.File(file.Value.Content, file.Value.ContentType);
        }).AllowAnonymous();
        return endpoints;
    }
}
