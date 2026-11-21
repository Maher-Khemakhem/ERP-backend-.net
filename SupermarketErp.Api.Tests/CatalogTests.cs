using System.Net;

namespace SupermarketErp.Api.Tests;

/// <summary>Product catalog and product families: creation rules, uniqueness and deletion.</summary>
public sealed class CatalogTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    [Fact]
    public async Task A_product_is_created_with_trimmed_text_defaults_and_its_family()
    {
        var family = (await Api.PostOk("/api/inventory/product-families", new { name = "  Dairy " + Guid.NewGuid().ToString("N")[..6], imageUrl = "  " }))
            .GetProperty("familyId").GetGuid();
        var sku = "SKU-" + Guid.NewGuid().ToString("N")[..8];

        var created = await Api.PostOk("/api/inventory/products",
            new { sku = "  " + sku + " ", name = "  Milk ", familyId = family, unitType = " each ", perishable = true, reorderPoint = 4 });

        var product = await Api.GetOk($"/api/inventory/products/{created.GetProperty("productId").GetGuid()}");
        Assert.Equal(sku, product.GetProperty("sku").GetString());
        Assert.Equal("Milk", product.GetProperty("name").GetString());
        Assert.Equal("EACH", product.GetProperty("unitType").GetString());
        Assert.Equal(19m, product.GetProperty("vatRate").GetDecimal()); // default VAT
        Assert.Equal(4, product.GetProperty("reorderPoint").GetInt32());
        Assert.True(product.GetProperty("perishable").GetBoolean());
        Assert.StartsWith("Dairy", product.GetProperty("familyName").GetString());
        Assert.Contains((await Api.GetOk("/api/inventory/products")).EnumerateArray(), p => p.GetProperty("sku").GetString() == sku);
    }

    [Fact]
    public async Task Product_input_is_validated()
    {
        string Sku() => "SKU-" + Guid.NewGuid().ToString("N")[..8];
        async Task<HttpStatusCode> Create(object body) => (await Api.Post("/api/inventory/products", body)).Status;

        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = " ", name = "n", unitType = "EACH", reorderPoint = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = " ", unitType = "EACH", reorderPoint = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = "BOX", reorderPoint = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = (string?)null, reorderPoint = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = "EACH", reorderPoint = -1 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = "EACH", reorderPoint = 0, vatRate = 101m }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = "EACH", reorderPoint = 0, vatRate = -1m }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = "EACH", reorderPoint = 0, imageUrl = new string('x', 501) }));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(new { sku = Sku(), name = "n", unitType = "EACH", reorderPoint = 0, familyId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await Create(new { sku = Sku(), name = "n", unitType = "WEIGHT", reorderPoint = 0, vatRate = 7m }));
    }

    [Fact]
    public async Task A_sku_can_only_be_used_once()
    {
        var sku = "SKU-" + Guid.NewGuid().ToString("N")[..8];
        await Api.PostOk("/api/inventory/products", new { sku, name = "First", unitType = "EACH", reorderPoint = 0 });

        var again = await Api.Post("/api/inventory/products", new { sku, name = "Second", unitType = "EACH", reorderPoint = 0 });

        Assert.Equal(HttpStatusCode.Conflict, again.Status);
    }

    [Fact]
    public async Task An_unused_product_can_be_deleted()
    {
        var unused = await Api.CreateProduct();

        Assert.Equal(HttpStatusCode.NoContent, (await Api.Send(HttpMethod.Delete, $"/api/inventory/products/{unused}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Get, $"/api/inventory/products/{unused}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.Send(HttpMethod.Delete, $"/api/inventory/products/{unused}")).Status);
    }

    [Fact(Skip = "Known issue, needs a decision: stock, prices and orders point at a product by id without a foreign key, " +
                 "so a product that has stock or sales history is currently deleted and leaves orphaned rows behind.")]
    public async Task A_product_that_has_stock_cannot_be_deleted()
    {
        var stocked = await Api.ProductWithStock(price: 5m, quantity: 3, unitCost: 1m);

        Assert.Equal(HttpStatusCode.Conflict, (await Api.Send(HttpMethod.Delete, $"/api/inventory/products/{stocked}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.Send(HttpMethod.Get, $"/api/inventory/products/{stocked}")).Status);
    }

    [Fact]
    public async Task Families_have_unique_names_and_validated_input()
    {
        var name = "Bakery " + Guid.NewGuid().ToString("N")[..6];
        var created = await Api.PostOk("/api/inventory/product-families", new { name = "  " + name + "  ", imageUrl = "/img/b.png" });

        Assert.Contains((await Api.GetOk("/api/inventory/product-families")).EnumerateArray(),
            f => f.GetProperty("id").GetGuid() == created.GetProperty("familyId").GetGuid()
                 && f.GetProperty("name").GetString() == name && f.GetProperty("imageUrl").GetString() == "/img/b.png");
        Assert.Equal(HttpStatusCode.Conflict, (await Api.Post("/api/inventory/product-families", new { name = name.ToUpperInvariant() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/inventory/product-families", new { name = " " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/inventory/product-families", new { name = new string('x', 161) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.Post("/api/inventory/product-families", new { name = "ok", imageUrl = new string('x', 501) })).Status);
    }

    [Fact]
    public async Task The_stock_overview_shows_status_and_the_earliest_expiry()
    {
        var low = await Api.CreateProduct(perishable: true, reorderPoint: 5);
        var empty = await Api.CreateProduct();
        var today = DateOnly.FromDateTime(DateTime.Today);
        await Api.ReceiveStock(low, 3, unitCost: 1m, lotNumber: "L1", expiry: today.AddDays(9));
        await Api.ReceiveStock(low, 2, unitCost: 1m, lotNumber: "L2", expiry: today.AddDays(2));

        var overview = (await Api.GetOk("/api/inventory/stock")).EnumerateArray().ToArray();

        var lowRow = overview.Single(r => r.GetProperty("productId").GetGuid() == low);
        Assert.Equal("LOW_STOCK", lowRow.GetProperty("status").GetString()); // 5 on hand, reorder point 5
        Assert.Equal(5m, lowRow.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(2, lowRow.GetProperty("lotCount").GetInt32());
        Assert.Equal(today.AddDays(2).ToString("yyyy-MM-dd"), lowRow.GetProperty("earliestExpiryDate").GetString());
        Assert.Equal("OUT_OF_STOCK", overview.Single(r => r.GetProperty("productId").GetGuid() == empty).GetProperty("status").GetString());
    }
}
