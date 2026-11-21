using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SupermarketErp.Api.Features.Auth;
using SupermarketErp.Api.Features.Inventory;
using SupermarketErp.Api.Features.Pricing;
using SupermarketErp.Api.Infrastructure;

namespace SupermarketErp.Api.Features.Ecommerce;

public sealed class StorefrontOrderService(
    BusinessDbContext db,
    IConfiguration configuration,
    IInventoryStock inventory,
    IPricingCalculator pricing,
    IAuthService users)
    : IStorefrontOrderService
{
    private readonly int timeout = ReadPositive(configuration, "ECOMMERCE_PAYMENT_TIMEOUT_MINUTES", 15);
    private async Task<IDbContextTransaction> Tx(CancellationToken ct) => await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
    public async Task<CartView> GetCart(Guid customer, CancellationToken ct) { var c=await Cart(customer,ct); return await CartView(c.Id,ct); }
    public async Task<CartView> AddCartItem(Guid customer, QuantityRequest r, CancellationToken ct) { ValidateQuantity(r.QuantityValue,r.UnitType); await using var tx=await Tx(ct); var c=await Cart(customer,ct); var u=await Product(r.ProductId,r.UnitType,ct); var i=await db.CartItems.SingleOrDefaultAsync(x=>x.CartId==c.Id&&x.ProductId==r.ProductId,ct); if(i is null) db.CartItems.Add(new CartItemEntity{Id=Guid.NewGuid(),CartId=c.Id,ProductId=r.ProductId,Quantity=r.QuantityValue,UnitType=u}); else {i.Quantity+=r.QuantityValue;i.UnitType=u;} Touch(c); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await CartView(c.Id,ct); }
    public async Task<CartView> ChangeCartItem(Guid customer, QuantityRequest r, CancellationToken ct) { ValidateQuantity(r.QuantityValue,r.UnitType); await using var tx=await Tx(ct); var c=await RequireCart(customer,ct); var u=await Product(r.ProductId,r.UnitType,ct); var i=await db.CartItems.SingleOrDefaultAsync(x=>x.CartId==c.Id&&x.ProductId==r.ProductId,ct)??throw new InvalidOperationException("Cart item not found."); i.Quantity=r.QuantityValue;i.UnitType=u;Touch(c);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await CartView(c.Id,ct); }
    public async Task<CartView> RemoveCartItem(Guid customer, Guid product, CancellationToken ct) { await using var tx=await Tx(ct); var c=await RequireCart(customer,ct); var i=await db.CartItems.SingleOrDefaultAsync(x=>x.CartId==c.Id&&x.ProductId==product,ct);if(i is not null)db.CartItems.Remove(i);Touch(c);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await CartView(c.Id,ct); }
    public async Task<OnlineOrderView> Checkout(Guid customer, CancellationToken ct) { await using var tx=await Tx(ct);var c=await Cart(customer,ct);var rows=await Rows(c.Id,ct);if(rows.Count==0)throw new InvalidOperationException("Cannot check out an empty cart.");if(rows.Any(x=>!x.Visible||!x.HasPrice))throw new InvalidOperationException("The cart contains a product that is no longer available online.");var now=DateTimeOffset.UtcNow;var net=Money(rows.Sum(x=>Money(x.Price*x.Quantity)));var vat=Money(rows.Sum(x=>Money(Money(x.Price*x.Quantity)*x.Vat/100m)));var o=new OnlineOrderEntity{Id=Guid.NewGuid(),OrderNumber="ONL-"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),CustomerId=customer,Status="PENDING_PAYMENT",NetAmount=net,VatAmount=vat,TotalAmount=net+vat,CreatedAt=now,ExpiresAt=now.AddMinutes(timeout)};foreach(var x in rows){var n=Money(x.Price*x.Quantity);var v=Money(n*x.Vat/100m);await inventory.Reserve(x.ProductId,x.Quantity,x.Unit,ct);o.Lines.Add(new OnlineOrderLineEntity{Id=Guid.NewGuid(),OrderId=o.Id,ProductId=x.ProductId,ProductName=x.Name,Quantity=x.Quantity,UnitType=x.Unit,UnitPrice=x.Price,LineTotal=n+v,VatAmount=v});}db.OnlineOrders.Add(o);db.CartItems.RemoveRange(db.CartItems.Where(x=>x.CartId==c.Id));Touch(c);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await GetOrder(o.Id,ct); }
    public Task<OnlineOrderView> ConfirmDemoPayment(Guid c,Guid o,CancellationToken ct)=>Confirm(o,c,$"MOCK-CARD-{o}","CARD",true,ct); public Task<OnlineOrderView> ConfirmStripePayment(Guid o,string r,CancellationToken ct)=>Confirm(o,null,r,"STRIPE",false,ct);
    private async Task<OnlineOrderView> Confirm(Guid id,Guid? customer,string reference,string method,bool expire,CancellationToken ct){await using var tx=await Tx(ct);var o=await db.OnlineOrders.Include(x=>x.Lines).SingleOrDefaultAsync(x=>x.Id==id,ct);if(o is null||customer is not null&&o.CustomerId!=customer)throw new ArgumentException("Order not found.");if(o.Status=="CONFIRMED"){await tx.CommitAsync(ct);return await GetOrder(id,ct);}if(o.Status!="PENDING_PAYMENT")throw new InvalidOperationException("Only pending orders can be paid.");if(expire&&o.ExpiresAt<=DateTimeOffset.UtcNow){await Release(o.Lines,ct);o.Status="EXPIRED";o.ExpiredAt=DateTimeOffset.UtcNow;o.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await GetOrder(id,ct);}if(method=="CARD"&&await db.StripeCheckouts.AnyAsync(x=>x.OrderId==id,ct))throw new InvalidOperationException("This order has a Stripe Checkout; demo payment is unavailable.");decimal cogs=0;foreach(var l in o.Lines)cogs+=await inventory.ConsumeReserved(l.ProductId,l.Quantity,l.UnitType,ct);o.Status="CONFIRMED";o.PaymentReference=reference;o.ConfirmedAt=DateTimeOffset.UtcNow;o.Version++;await db.SaveChangesAsync(ct);await OutboxWriter.Write(db,"OnlineSaleConfirmed",new SaleCompletedEvent(id,Guid.Empty,method,o.NetAmount,o.VatAmount,o.TotalAmount,cogs),ct);await tx.CommitAsync(ct);return await GetOrder(id,ct);}
    public Task<IReadOnlyList<OnlineOrderView>> ListCustomerOrders(Guid c,CancellationToken ct)=>Orders(c,null,ct);public async Task<OnlineOrderView> GetCustomerOrder(Guid c,Guid o,CancellationToken ct)=>(await Orders(c,o,ct)).FirstOrDefault()??throw new ArgumentException($"Online order not found: {o}");public async Task<IReadOnlyList<StaffOnlineOrderView>> ListStaffOrders(CancellationToken ct){var a=await Orders(null,null,ct);var r=new List<StaffOnlineOrderView>();foreach(var o in a){var c=await Customer(o.Id,ct);r.Add(Staff(o,c));}return r;}public async Task<StaffOnlineOrderView> GetStaffOrder(Guid id,CancellationToken ct){var o=await GetOrder(id,ct);return Staff(o,await Customer(id,ct));}
    public Task<OnlineOrderView> CancelOrder(Guid c,Guid o,CancellationToken ct)=>Cancel(o,c,ct);public Task<StaffOnlineOrderView> CancelStaffOrder(Guid o,CancellationToken ct)=>CancelStaff(o,ct);
    public async Task ExpireDueOrders(CancellationToken ct){await using var tx=await Tx(ct);var a=await db.OnlineOrders.Include(x=>x.Lines).Where(x=>x.Status=="PENDING_PAYMENT"&&x.ExpiresAt<=DateTimeOffset.UtcNow).OrderBy(x=>x.ExpiresAt).Take(100).ToListAsync(ct);foreach(var o in a){await Release(o.Lines,ct);o.Status="EXPIRED";o.ExpiredAt=DateTimeOffset.UtcNow;o.Version++;}await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);}
    public async Task<StaffOnlineOrderView> AdvanceStatus(Guid id,string next,CancellationToken ct){var n=next.Trim().ToUpperInvariant();if(n is not("PREPARING"or"READY"))throw new ArgumentException("Staff can advance orders to PREPARING or READY.");await using var tx=await Tx(ct);var o=await db.OnlineOrders.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Online order not found: {id}");if(!((o.Status=="CONFIRMED"&&n=="PREPARING")||(o.Status=="PREPARING"&&n=="READY")))throw new InvalidOperationException($"Online order cannot move from {o.Status} to {n}.");o.Status=n;o.PickupCode=n=="READY"?Convert.ToHexString(RandomNumberGenerator.GetBytes(6)):null;o.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await GetStaffOrder(id,ct);}
    public async Task<StaffOnlineOrderView> CompletePickup(Guid id,string code,string staff,CancellationToken ct){await using var tx=await Tx(ct);var o=await db.OnlineOrders.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new ArgumentException($"Online order not found: {id}");if(o.Status!="READY"||string.IsNullOrWhiteSpace(code))throw new InvalidOperationException("Only ready orders can be collected with a pickup code.");var n=code.Replace("-","").Replace(" ","").ToUpperInvariant();if(!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(n),System.Text.Encoding.ASCII.GetBytes(o.PickupCode??"")))throw new ArgumentException("Invalid pickup code.");o.Status="COMPLETED";o.PickupCode=null;o.PickedUpAt=DateTimeOffset.UtcNow;o.PickedUpBy=staff;o.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await GetStaffOrder(id,ct);}
    private async Task<StaffOnlineOrderView> CancelStaff(Guid id,CancellationToken ct){var o=await Cancel(id,null,ct);return Staff(o,await Customer(id,ct));}private async Task<OnlineOrderView> Cancel(Guid id,Guid? customer,CancellationToken ct){await using var tx=await Tx(ct);var o=await db.OnlineOrders.Include(x=>x.Lines).SingleOrDefaultAsync(x=>x.Id==id,ct);if(o is null||customer is not null&&o.CustomerId!=customer)throw new ArgumentException($"Online order not found: {id}");if(o.Status is "CANCELLED"or"EXPIRED"){await tx.CommitAsync(ct);return await GetOrder(id,ct);}if(o.Status!="PENDING_PAYMENT")throw new InvalidOperationException("Only unpaid orders can be cancelled.");if(await db.StripeCheckouts.AnyAsync(x=>x.OrderId==id&&(x.Status=="CREATING"||x.Status=="PENDING"),ct))throw new InvalidOperationException("An active Stripe Checkout exists for this order.");await Release(o.Lines,ct);o.Status="CANCELLED";o.CancelledAt=DateTimeOffset.UtcNow;o.Version++;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await GetOrder(id,ct);}
    private async Task<OnlineOrderView> GetOrder(Guid id,CancellationToken ct)=>(await Orders(null,id,ct)).FirstOrDefault()??throw new ArgumentException($"Online order not found: {id}");private async Task<IReadOnlyList<OnlineOrderView>> Orders(Guid? customer,Guid? id,CancellationToken ct){var q=db.OnlineOrders.AsNoTracking().Include(x=>x.Lines).AsQueryable();if(customer is not null)q=q.Where(x=>x.CustomerId==customer);if(id is not null)q=q.Where(x=>x.Id==id);var a=await q.OrderByDescending(x=>x.CreatedAt).ToListAsync(ct);var ids=a.Select(x=>x.Id).ToArray();var ss=await db.StripeCheckouts.AsNoTracking().Where(x=>ids.Contains(x.OrderId)).ToDictionaryAsync(x=>x.OrderId,x=>x.Status,ct);return a.Select(x=>View(x,ss.GetValueOrDefault(x.Id))).ToList();}
    private static OnlineOrderView View(OnlineOrderEntity o,string? stripe)=>new(o.Id,o.OrderNumber,o.Status,MoneyText(o.NetAmount),MoneyText(o.VatAmount),MoneyText(o.TotalAmount),o.PaymentReference,JavaInstantFormat.Format(o.CreatedAt),o.ConfirmedAt is null?null:JavaInstantFormat.Format(o.ConfirmedAt.Value),JavaInstantFormat.Format(o.ExpiresAt),o.CancelledAt is null?null:JavaInstantFormat.Format(o.CancelledAt.Value),o.ExpiredAt is null?null:JavaInstantFormat.Format(o.ExpiredAt.Value),stripe,o.PickupCode is null?null:$"{o.PickupCode[..6]}-{o.PickupCode[6..]}",o.Lines.OrderBy(x=>x.Id).Select(x=>new OrderLineView(x.ProductId,x.ProductName,QtyText(x.Quantity,x.UnitType),x.UnitType,MoneyText(x.UnitPrice),MoneyText(x.LineTotal),MoneyText(x.VatAmount))).ToList());
    private static StaffOnlineOrderView Staff(OnlineOrderView o,(Guid Id,string Username,string Name,string? At,string? By)c)=>new(o.Id,o.OrderNumber,c.Id,c.Username,c.Name,o.Status,o.NetAmount,o.VatAmount,o.TotalAmount,o.PaymentReference,o.CreatedAt,o.ConfirmedAt,o.ExpiresAt,o.CancelledAt,o.ExpiredAt,c.At,c.By,o.Lines);
    private async Task<(Guid Id,string Username,string Name,string? At,string? By)> Customer(Guid id,CancellationToken ct)
    {
        var fulfilment = await db.OnlineOrders.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.CustomerId, x.PickedUpAt, x.PickedUpBy }).SingleOrDefaultAsync(ct);
        var user = fulfilment is null ? null : await users.FindById(fulfilment.CustomerId, ct);
        return fulfilment is null || user is null
            ? (Guid.Empty, "Unknown", "Unknown customer", null, null)
            : (user.Id, user.Username, user.DisplayName,
               fulfilment.PickedUpAt is null ? null : JavaInstantFormat.Format(fulfilment.PickedUpAt.Value), fulfilment.PickedUpBy);
    }
    private async Task<CartEntity> Cart(Guid customer,CancellationToken ct){var c=await db.Carts.SingleOrDefaultAsync(x=>x.CustomerId==customer,ct);if(c is not null)return c;c=new CartEntity{Id=Guid.NewGuid(),CustomerId=customer,CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow};db.Carts.Add(c);await db.SaveChangesAsync(ct);return c;}private async Task<CartEntity> RequireCart(Guid c,CancellationToken ct)=>await db.Carts.SingleOrDefaultAsync(x=>x.CustomerId==c,ct)??throw new InvalidOperationException("Cart not found.");private static void Touch(CartEntity c){c.UpdatedAt=DateTimeOffset.UtcNow;c.Version++;}
    private async Task<string> Product(Guid id,string req,CancellationToken ct)
    {
        var unit = req.Trim().ToUpperInvariant();
        if (unit is not ("EACH" or "WEIGHT")) throw new ArgumentException("Unit type must be EACH or WEIGHT.");
        var product = await inventory.FindProduct(id, ct);
        if (product is null || !product.OnlineVisible) throw new ArgumentException("Product is not available in the online catalog.");
        if (product.UnitType != unit) throw new ArgumentException("Requested unit type does not match the product.");
        return unit;
    }
    private async Task<CartView> CartView(Guid id,CancellationToken ct){var a=await Rows(id,ct);decimal sub=0,vat=0;var items=a.Select(x=>{var l=Money(x.Price*x.Quantity);var v=Money(l*x.Vat/100m);sub+=l;vat+=v;return new CartItemView(x.ProductId,x.Name,QtyText(x.Quantity,x.Unit),x.Unit,MoneyText(x.Price),MoneyText(l),x.Available,x.Available>0);}).ToList();sub=Money(sub);vat=Money(vat);return new(items,MoneyText(sub),MoneyText(vat),MoneyText(sub+vat));}
    private async Task<List<Row>> Rows(Guid id,CancellationToken ct)
    {
        var items = await db.CartItems.AsNoTracking().Where(x => x.CartId == id).ToListAsync(ct);
        var ids = items.Select(x => x.ProductId).ToArray();
        if (ids.Length == 0) return [];
        var products = await inventory.FindProducts(ids, ct);
        var prices = await pricing.CurrentUnitPrices(ids, ct);
        var available = await inventory.AvailableQuantities(ids, ct);
        return items.Where(x => products.ContainsKey(x.ProductId)).OrderBy(x => products[x.ProductId].Name).Select(x =>
        {
            var product = products[x.ProductId];
            var hasPrice = prices.TryGetValue(x.ProductId, out var price);
            return new Row(x.ProductId, product.Name, x.Quantity, x.UnitType, hasPrice ? price : 0, product.VatRate,
                available.GetValueOrDefault(x.ProductId), product.OnlineVisible, hasPrice);
        }).ToList();
    }
    private async Task Release(IEnumerable<OnlineOrderLineEntity> lines,CancellationToken ct){foreach(var l in lines)await inventory.Release(l.ProductId,l.Quantity,l.UnitType,ct);}
    private static void ValidateQuantity(decimal v,string u){if(v<=0)throw new ArgumentException("Quantity must be positive.");var t=u.Trim().ToUpperInvariant();if(t is not("EACH"or"WEIGHT"))throw new ArgumentException("Unit type must be EACH or WEIGHT.");if(t=="EACH"&&v!=decimal.Truncate(v))throw new ArgumentException("EACH quantity must be a whole number.");if(t=="WEIGHT"&&decimal.Round(v,3)!=v)throw new ArgumentException("WEIGHT quantity supports three decimal places.");}private static decimal Money(decimal x)=>decimal.Round(x,3,MidpointRounding.AwayFromZero);private static string MoneyText(decimal x)=>$"{x.ToString("0.000",CultureInfo.InvariantCulture)} TND";private static string QtyText(decimal x,string u)=>u=="EACH"?$"{x:0} unit(s)":$"{x:0.000} kg";private static int ReadPositive(IConfiguration c,string k,int f)=>int.TryParse(c[k],out var v)&&v>=1?v:c[k] is null?f:throw new ArgumentException($"{k} must be at least one minute.");
    private sealed record Row(Guid ProductId,string Name,decimal Quantity,string Unit,decimal Price,decimal Vat,decimal Available,bool Visible,bool HasPrice);
}
public sealed class OnlineOrderExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<OnlineOrderExpiryWorker> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly int _scanIntervalMs = int.TryParse(configuration["ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS"], out var interval)
        && interval > 0 ? interval : configuration["ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS"] is null ? 60_000
        : throw new ArgumentException("ECOMMERCE_EXPIRY_SCAN_INTERVAL_MS must be positive.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IStorefrontOrderService>();
                await service.ExpireDueOrders(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Failed to expire online orders"); }
            await Task.Delay(TimeSpan.FromMilliseconds(_scanIntervalMs), stoppingToken);
        }
    }
}





