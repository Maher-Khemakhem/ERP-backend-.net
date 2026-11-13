using Microsoft.EntityFrameworkCore;

namespace SupermarketErp.Api.Infrastructure;

/// <summary>
/// EF model for the business tables.  The database is provisioned by
/// IdentityDatabaseInitializer; this context deliberately does not own schema creation.
/// </summary>
public sealed class BusinessDbContext(DbContextOptions<BusinessDbContext> options) : DbContext(options)
{
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<ProductFamilyEntity> ProductFamilies => Set<ProductFamilyEntity>();
    public DbSet<ProductPriceEntity> ProductPrices => Set<ProductPriceEntity>();
    public DbSet<PromotionEntity> Promotions => Set<PromotionEntity>();
    public DbSet<StockLevelEntity> StockLevels => Set<StockLevelEntity>();
    public DbSet<LotEntryEntity> LotEntries => Set<LotEntryEntity>();
    public DbSet<LotEntity> Lots => Set<LotEntity>();
    public DbSet<SupplierEntity> Suppliers => Set<SupplierEntity>();
    public DbSet<PurchaseOrderEntity> PurchaseOrders => Set<PurchaseOrderEntity>();
    public DbSet<PurchaseOrderLineEntity> PurchaseOrderLines => Set<PurchaseOrderLineEntity>();
    public DbSet<LowStockAlertEntity> LowStockAlerts => Set<LowStockAlertEntity>();
    public DbSet<JournalEntryEntity> JournalEntries => Set<JournalEntryEntity>();
    public DbSet<JournalLineEntity> JournalLines => Set<JournalLineEntity>();
    public DbSet<LedgerEntryEntity> LedgerEntries => Set<LedgerEntryEntity>();
    public DbSet<SupplierInvoiceEntity> SupplierInvoices => Set<SupplierInvoiceEntity>();
    public DbSet<SupplierPaymentEntity> SupplierPayments => Set<SupplierPaymentEntity>();
    public DbSet<DotnetOutboxEntity> DotnetOutbox => Set<DotnetOutboxEntity>();
    public DbSet<TillEntity> Tills => Set<TillEntity>();
    public DbSet<CashSessionEntity> CashSessions => Set<CashSessionEntity>();
    public DbSet<PosTransactionEntity> PosTransactions => Set<PosTransactionEntity>();
    public DbSet<TransactionLineEntity> TransactionLines => Set<TransactionLineEntity>();
    public DbSet<CartEntity> Carts => Set<CartEntity>();
    public DbSet<CartItemEntity> CartItems => Set<CartItemEntity>();
    public DbSet<OnlineOrderEntity> OnlineOrders => Set<OnlineOrderEntity>();
    public DbSet<OnlineOrderLineEntity> OnlineOrderLines => Set<OnlineOrderLineEntity>();
    public DbSet<StripeCheckoutEntity> StripeCheckouts => Set<StripeCheckoutEntity>();
    public DbSet<AppUserEntity> AppUsers => Set<AppUserEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var family = b.Entity<ProductFamilyEntity>();
        family.ToTable("product_family", "inventory").HasKey(x => x.Id);
        family.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        family.Property(x => x.Name).HasColumnName("name").HasMaxLength(160).IsUnicode(false);
        family.Property(x => x.ImageUrl).HasColumnName("image_url").HasMaxLength(500).IsUnicode(false);

        var product = b.Entity<ProductEntity>();
        product.ToTable("product", "inventory").HasKey(x => x.Id);
        product.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        product.Property(x => x.Sku).HasColumnName("sku").HasMaxLength(255).IsUnicode(false);
        product.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsUnicode(false);
        product.Property(x => x.FamilyId).HasColumnName("family_id");
        product.Property(x => x.ImageUrl).HasColumnName("image_url").HasMaxLength(500).IsUnicode(false);
        product.Property(x => x.UnitType).HasColumnName("unit_type").HasMaxLength(255).IsUnicode(false);
        product.Property(x => x.Perishable).HasColumnName("perishable");
        product.Property(x => x.ReorderPoint).HasColumnName("reorder_point");
        product.Property(x => x.VatRate).HasColumnName("vat_rate").HasPrecision(5, 2);
        product.Property(x => x.OnlineVisible).HasColumnName("online_visible");
        product.HasOne(x => x.Family).WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.NoAction);

        var price = b.Entity<ProductPriceEntity>();
        price.ToTable("product_price", "pricing").HasKey(x => x.Id);
        price.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        price.Property(x => x.ProductId).HasColumnName("product_id");
        price.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(38, 2);

        var promotion = b.Entity<PromotionEntity>();
        promotion.ToTable("promotion", "pricing").HasKey(x => x.Id);
        promotion.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        promotion.Property(x => x.ProductId).HasColumnName("product_id");
        promotion.Property(x => x.Type).HasColumnName("type").HasMaxLength(255).IsUnicode(false);
        promotion.Property(x => x.DiscountPercent).HasColumnName("discount_percent").HasPrecision(38, 2);
        promotion.Property(x => x.ValidFrom).HasColumnName("valid_from");
        promotion.Property(x => x.ValidUntil).HasColumnName("valid_until");
        promotion.Property(x => x.Active).HasColumnName("active");

        var stock = b.Entity<StockLevelEntity>();
        stock.ToTable("stock_level", "inventory").HasKey(x => x.Id);
        stock.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        stock.Property(x => x.ProductId).HasColumnName("product_id");
        stock.Property(x => x.Version).HasColumnName("version");

        var entry = b.Entity<LotEntryEntity>();
        entry.ToTable("lot_entry", "inventory").HasKey(x => x.Id);
        entry.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        entry.Property(x => x.StockLevelId).HasColumnName("stock_level_id");
        entry.Property(x => x.LotId).HasColumnName("lot_id");
        entry.Property(x => x.OnHandValue).HasColumnName("on_hand_value").HasPrecision(38, 2);
        entry.Property(x => x.OnHandUnitType).HasColumnName("on_hand_unit_type");
        entry.Property(x => x.ReservedValue).HasColumnName("reserved_value").HasPrecision(38, 2);
        entry.Property(x => x.ReservedUnitType).HasColumnName("reserved_unit_type");

        var lot = b.Entity<LotEntity>();
        lot.ToTable("lot", "inventory").HasKey(x => x.Id);
        lot.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        lot.Property(x => x.LotNumber).HasColumnName("lot_number").HasMaxLength(255).IsUnicode(false);
        lot.Property(x => x.ProductId).HasColumnName("product_id");
        lot.Property(x => x.ExpiryDate).HasColumnName("expiry_date");
        lot.Property(x => x.ReceivedDate).HasColumnName("received_date");
        lot.Property(x => x.SupplierReference).HasColumnName("supplier_reference").HasMaxLength(255).IsUnicode(false);
        lot.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(38, 2);

        var supplier = b.Entity<SupplierEntity>();
        supplier.ToTable("supplier", "procurement").HasKey(x => x.Id);
        supplier.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        supplier.Property(x => x.Name).HasColumnName("name").HasMaxLength(255).IsUnicode(false);
        supplier.Property(x => x.ContactEmail).HasColumnName("contact_email").HasMaxLength(255).IsUnicode(false);
        supplier.Property(x => x.ContactPhone).HasColumnName("contact_phone").HasMaxLength(100).IsUnicode(false);
        supplier.Property(x => x.Address).HasColumnName("address").HasMaxLength(500).IsUnicode(false);

        var po = b.Entity<PurchaseOrderEntity>();
        po.ToTable("purchase_order", "procurement").HasKey(x => x.Id);
        po.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        po.Property(x => x.SupplierId).HasColumnName("supplier_id");
        po.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsUnicode(false);
        po.Property(x => x.PlacedAt).HasColumnName("placed_at");
        po.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        var pol = b.Entity<PurchaseOrderLineEntity>();
        pol.ToTable("purchase_order_line", "procurement").HasKey(x => x.Id);
        pol.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        pol.Property(x => x.PurchaseOrderId).HasColumnName("purchase_order_id");
        pol.Property(x => x.ProductId).HasColumnName("product_id");
        pol.Property(x => x.Quantity).HasColumnName("value").HasPrecision(38, 2);
        pol.Property(x => x.UnitType).HasColumnName("unit_type");
        pol.Property(x => x.UnitCost).HasColumnName("unit_cost").HasPrecision(38, 2);
        var alert = b.Entity<LowStockAlertEntity>();
        alert.ToTable("low_stock_alert", "procurement").HasKey(x => x.Id);
        alert.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        alert.Property(x => x.ProductId).HasColumnName("product_id");
        alert.Property(x => x.RaisedAt).HasColumnName("raised_at");
        alert.Property(x => x.Acknowledged).HasColumnName("acknowledged");

        MapFinance(b);

        var till = b.Entity<TillEntity>(); till.ToTable("till", "pos").HasKey(x => x.Id);
        till.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        till.Property(x => x.TillNumber).HasColumnName("till_number"); till.Property(x => x.Status).HasColumnName("status"); till.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        var session = b.Entity<CashSessionEntity>(); session.ToTable("cash_session", "pos").HasKey(x => x.Id);
        session.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); session.Property(x => x.TillId).HasColumnName("till_id"); session.Property(x => x.CashierName).HasColumnName("cashier_name");
        session.Property(x => x.OpeningFloat).HasColumnName("opening_float").HasPrecision(38, 2); session.Property(x => x.ExpectedCash).HasColumnName("expected_cash").HasPrecision(38, 2); session.Property(x => x.CountedCash).HasColumnName("counted_cash").HasPrecision(38, 2);
        session.Property(x => x.Status).HasColumnName("status"); session.Property(x => x.OpenedAt).HasColumnName("opened_at"); session.Property(x => x.ClosedAt).HasColumnName("closed_at"); session.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        var transaction = b.Entity<PosTransactionEntity>(); transaction.ToTable("pos_transaction", "pos").HasKey(x => x.Id);
        transaction.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); transaction.Property(x => x.CashSessionId).HasColumnName("cash_session_id"); transaction.Property(x => x.Status).HasColumnName("status"); transaction.Property(x => x.PaymentMethod).HasColumnName("payment_method"); transaction.Property(x => x.StartedAt).HasColumnName("started_at"); transaction.Property(x => x.CompletedAt).HasColumnName("completed_at"); transaction.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        var line = b.Entity<TransactionLineEntity>(); line.ToTable("transaction_line", "pos").HasKey(x => x.Id);
        line.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); line.Property(x => x.TransactionId).HasColumnName("transaction_id"); line.Property(x => x.ProductId).HasColumnName("product_id"); line.Property(x => x.Quantity).HasColumnName("value").HasPrecision(38, 3); line.Property(x => x.UnitType).HasColumnName("unit_type"); line.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(38, 3); line.Property(x => x.VatAmount).HasColumnName("vat_amount").HasPrecision(38, 3);
        transaction.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);

        var cart = b.Entity<CartEntity>(); cart.ToTable("cart", "ecommerce").HasKey(x => x.Id); cart.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); cart.Property(x => x.CustomerId).HasColumnName("customer_id"); cart.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken(); cart.Property(x => x.CreatedAt).HasColumnName("created_at"); cart.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        var cartItem = b.Entity<CartItemEntity>(); cartItem.ToTable("cart_item", "ecommerce").HasKey(x => x.Id); cartItem.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); cartItem.Property(x => x.CartId).HasColumnName("cart_id"); cartItem.Property(x => x.ProductId).HasColumnName("product_id"); cartItem.Property(x => x.Quantity).HasColumnName("quantity_value").HasPrecision(38, 3); cartItem.Property(x => x.UnitType).HasColumnName("quantity_unit_type");
        var order = b.Entity<OnlineOrderEntity>(); order.ToTable("online_order", "ecommerce").HasKey(x => x.Id); order.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); order.Property(x => x.OrderNumber).HasColumnName("order_number"); order.Property(x => x.CustomerId).HasColumnName("customer_id"); order.Property(x => x.Status).HasColumnName("status"); order.Property(x => x.NetAmount).HasColumnName("net_amount").HasPrecision(38, 3); order.Property(x => x.VatAmount).HasColumnName("vat_amount").HasPrecision(38, 3); order.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(38, 3); order.Property(x => x.PaymentReference).HasColumnName("payment_reference"); order.Property(x => x.CreatedAt).HasColumnName("created_at"); order.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at"); order.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken(); order.Property(x => x.ExpiresAt).HasColumnName("expires_at"); order.Property(x => x.CancelledAt).HasColumnName("cancelled_at"); order.Property(x => x.ExpiredAt).HasColumnName("expired_at"); order.Property(x => x.PickupCode).HasColumnName("pickup_code"); order.Property(x => x.PickedUpAt).HasColumnName("picked_up_at"); order.Property(x => x.PickedUpBy).HasColumnName("picked_up_by"); order.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        var orderLine = b.Entity<OnlineOrderLineEntity>(); orderLine.ToTable("online_order_line", "ecommerce").HasKey(x => x.Id); orderLine.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); orderLine.Property(x => x.OrderId).HasColumnName("online_order_id"); orderLine.Property(x => x.ProductId).HasColumnName("product_id"); orderLine.Property(x => x.ProductName).HasColumnName("product_name"); orderLine.Property(x => x.Quantity).HasColumnName("quantity_value").HasPrecision(38, 3); orderLine.Property(x => x.UnitType).HasColumnName("quantity_unit_type"); orderLine.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(38, 3); orderLine.Property(x => x.LineTotal).HasColumnName("line_total").HasPrecision(38, 3); orderLine.Property(x => x.VatAmount).HasColumnName("vat_amount").HasPrecision(38, 3);
        var checkout = b.Entity<StripeCheckoutEntity>(); checkout.ToTable("stripe_checkout", "ecommerce").HasKey(x => x.Id); checkout.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); checkout.Property(x => x.OrderId).HasColumnName("order_id"); checkout.Property(x => x.SessionId).HasColumnName("checkout_session_id"); checkout.Property(x => x.Url).HasColumnName("checkout_url"); checkout.Property(x => x.TndAmount).HasColumnName("tnd_amount").HasPrecision(38, 3); checkout.Property(x => x.AmountCents).HasColumnName("eur_cents"); checkout.Property(x => x.Rate).HasColumnName("tnd_to_eur_rate").HasPrecision(38, 6); checkout.Property(x => x.Status).HasColumnName("status"); checkout.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken(); checkout.Property(x => x.CreatedAt).HasColumnName("created_at");
        var user = b.Entity<AppUserEntity>(); user.ToTable("app_user", "dbo").HasKey(x => x.Id); user.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); user.Property(x => x.Username).HasColumnName("username"); user.Property(x => x.DisplayName).HasColumnName("display_name");
    }

    private static void MapFinance(ModelBuilder b)
    {
        var je = b.Entity<JournalEntryEntity>(); je.ToTable("journal_entry", "finance").HasKey(x => x.Id);
        je.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); je.Property(x => x.EventKey).HasColumnName("event_key");
        je.Property(x => x.Description).HasColumnName("description"); je.Property(x => x.ReferenceId).HasColumnName("reference_id"); je.Property(x => x.PostedAt).HasColumnName("posted_at");
        je.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
        var jl = b.Entity<JournalLineEntity>(); jl.ToTable("journal_line", "finance").HasKey(x => x.Id);
        jl.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); jl.Property(x => x.JournalEntryId).HasColumnName("journal_entry_id");
        jl.Property(x => x.AccountCode).HasColumnName("account_code"); jl.Property(x => x.DebitAmount).HasColumnName("debit_amount").HasPrecision(38, 2); jl.Property(x => x.CreditAmount).HasColumnName("credit_amount").HasPrecision(38, 2); jl.Property(x => x.Description).HasColumnName("description");
        var le = b.Entity<LedgerEntryEntity>(); le.ToTable("ledger_entry", "finance").HasKey(x => x.Id);
        le.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); le.Property(x => x.Type).HasColumnName("type"); le.Property(x => x.Amount).HasColumnName("amount").HasPrecision(38, 2); le.Property(x => x.Description).HasColumnName("description"); le.Property(x => x.ReferenceId).HasColumnName("reference_id"); le.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        var si = b.Entity<SupplierInvoiceEntity>(); si.ToTable("supplier_invoice", "finance").HasKey(x => x.Id);
        si.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); si.Property(x => x.SupplierId).HasColumnName("supplier_id"); si.Property(x => x.PurchaseOrderId).HasColumnName("purchase_order_id"); si.Property(x => x.InvoiceNumber).HasColumnName("invoice_number"); si.Property(x => x.Subtotal).HasColumnName("subtotal").HasPrecision(38, 2); si.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasPrecision(38, 2); si.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(38, 2); si.Property(x => x.PaidAmount).HasColumnName("paid_amount").HasPrecision(38, 2); si.Property(x => x.Status).HasColumnName("status"); si.Property(x => x.IssueDate).HasColumnName("issue_date"); si.Property(x => x.DueDate).HasColumnName("due_date"); si.Property(x => x.CreatedAt).HasColumnName("created_at"); si.Property(x => x.Version).HasColumnName("version");
        var sp = b.Entity<SupplierPaymentEntity>(); sp.ToTable("supplier_payment", "finance").HasKey(x => x.Id);
        sp.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); sp.Property(x => x.PaymentReference).HasColumnName("payment_reference"); sp.Property(x => x.InvoiceId).HasColumnName("invoice_id"); sp.Property(x => x.Amount).HasColumnName("amount").HasPrecision(38, 2); sp.Property(x => x.PaymentMethod).HasColumnName("payment_method"); sp.Property(x => x.PaidDate).HasColumnName("paid_date");
        var ob = b.Entity<DotnetOutboxEntity>(); ob.ToTable("dotnet_outbox", "platform").HasKey(x => x.Id);
        ob.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever(); ob.Property(x => x.EventType).HasColumnName("event_type"); ob.Property(x => x.Payload).HasColumnName("payload"); ob.Property(x => x.OccurredAt).HasColumnName("occurred_at");
    }
}

public sealed class ProductEntity
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid? FamilyId { get; set; }
    public ProductFamilyEntity? Family { get; set; }
    public string? ImageUrl { get; set; }
    public string UnitType { get; set; } = "";
    public bool Perishable { get; set; }
    public int? ReorderPoint { get; set; }
    public decimal? VatRate { get; set; }
    public bool OnlineVisible { get; set; }
}
public sealed class ProductFamilyEntity { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? ImageUrl { get; set; } }
public sealed class ProductPriceEntity { public Guid Id { get; set; } public Guid ProductId { get; set; } public decimal? UnitPrice { get; set; } }
public sealed class PromotionEntity
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Type { get; set; } = "";
    public decimal DiscountPercent { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public bool Active { get; set; }
}
public sealed class StockLevelEntity { public Guid Id { get; set; } public Guid ProductId { get; set; } public long Version { get; set; } }
public sealed class LotEntryEntity
{
    public Guid Id { get; set; }
    public Guid? StockLevelId { get; set; }
    public Guid? LotId { get; set; }
    public decimal? OnHandValue { get; set; }
    public short? OnHandUnitType { get; set; }
    public decimal? ReservedValue { get; set; }
    public short? ReservedUnitType { get; set; }
}
public sealed class LotEntity { public Guid Id { get; set; } public string LotNumber { get; set; } = ""; public Guid ProductId { get; set; } public DateOnly? ExpiryDate { get; set; } public DateOnly ReceivedDate { get; set; } public string? SupplierReference { get; set; } public decimal UnitCost { get; set; } }
public sealed class SupplierEntity { public Guid Id { get; set; } public string Name { get; set; } = ""; public string? ContactEmail { get; set; } public string? ContactPhone { get; set; } public string? Address { get; set; } }
public sealed class PurchaseOrderEntity { public Guid Id { get; set; } public Guid SupplierId { get; set; } public string Status { get; set; } = ""; public DateTimeOffset PlacedAt { get; set; } public List<PurchaseOrderLineEntity> Lines { get; set; } = []; }
public sealed class PurchaseOrderLineEntity { public Guid Id { get; set; } public Guid PurchaseOrderId { get; set; } public Guid ProductId { get; set; } public decimal Quantity { get; set; } public short UnitType { get; set; } public decimal UnitCost { get; set; } }
public sealed class LowStockAlertEntity { public Guid Id { get; set; } public Guid ProductId { get; set; } public DateTimeOffset RaisedAt { get; set; } public bool Acknowledged { get; set; } }
public sealed class JournalEntryEntity { public Guid Id { get; set; } public string EventKey { get; set; } = ""; public string Description { get; set; } = ""; public Guid? ReferenceId { get; set; } public DateTimeOffset PostedAt { get; set; } public List<JournalLineEntity> Lines { get; set; } = []; }
public sealed class JournalLineEntity { public Guid Id { get; set; } public Guid JournalEntryId { get; set; } public string AccountCode { get; set; } = ""; public decimal DebitAmount { get; set; } public decimal CreditAmount { get; set; } public string Description { get; set; } = ""; }
public sealed class LedgerEntryEntity { public Guid Id { get; set; } public string Type { get; set; } = ""; public decimal Amount { get; set; } public string Description { get; set; } = ""; public Guid? ReferenceId { get; set; } public DateTimeOffset RecordedAt { get; set; } }
public sealed class SupplierInvoiceEntity { public Guid Id { get; set; } public Guid SupplierId { get; set; } public Guid? PurchaseOrderId { get; set; } public string InvoiceNumber { get; set; } = ""; public decimal Subtotal { get; set; } public decimal TaxAmount { get; set; } public decimal TotalAmount { get; set; } public decimal PaidAmount { get; set; } public string Status { get; set; } = ""; public DateOnly IssueDate { get; set; } public DateOnly? DueDate { get; set; } public DateTimeOffset CreatedAt { get; set; } public long Version { get; set; } }
public sealed class SupplierPaymentEntity { public Guid Id { get; set; } public string PaymentReference { get; set; } = ""; public Guid InvoiceId { get; set; } public decimal Amount { get; set; } public string PaymentMethod { get; set; } = ""; public DateOnly PaidDate { get; set; } }
public sealed class DotnetOutboxEntity { public Guid Id { get; set; } public string EventType { get; set; } = ""; public string Payload { get; set; } = ""; public DateTimeOffset OccurredAt { get; set; } }
public sealed class TillEntity { public Guid Id { get; set; } public string TillNumber { get; set; } = ""; public string Status { get; set; } = ""; public long Version { get; set; } }
public sealed class CashSessionEntity { public Guid Id { get; set; } public Guid TillId { get; set; } public string CashierName { get; set; } = ""; public decimal OpeningFloat { get; set; } public decimal ExpectedCash { get; set; } public decimal? CountedCash { get; set; } public string Status { get; set; } = ""; public DateTimeOffset OpenedAt { get; set; } public DateTimeOffset? ClosedAt { get; set; } public long Version { get; set; } }
public sealed class PosTransactionEntity { public Guid Id { get; set; } public Guid CashSessionId { get; set; } public string Status { get; set; } = ""; public string? PaymentMethod { get; set; } public DateTimeOffset StartedAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } public long Version { get; set; } public List<TransactionLineEntity> Lines { get; set; } = []; }
public sealed class TransactionLineEntity { public Guid Id { get; set; } public Guid TransactionId { get; set; } public Guid ProductId { get; set; } public decimal Quantity { get; set; } public short UnitType { get; set; } public decimal LineTotal { get; set; } public decimal VatAmount { get; set; } }
public sealed class CartEntity { public Guid Id { get; set; } public Guid CustomerId { get; set; } public long Version { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; } }
public sealed class CartItemEntity { public Guid Id { get; set; } public Guid CartId { get; set; } public Guid ProductId { get; set; } public decimal Quantity { get; set; } public string UnitType { get; set; } = ""; }
public sealed class OnlineOrderEntity { public Guid Id { get; set; } public string OrderNumber { get; set; } = ""; public Guid CustomerId { get; set; } public string Status { get; set; } = ""; public decimal NetAmount { get; set; } public decimal VatAmount { get; set; } public decimal TotalAmount { get; set; } public string? PaymentReference { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? ConfirmedAt { get; set; } public long Version { get; set; } public DateTimeOffset ExpiresAt { get; set; } public DateTimeOffset? CancelledAt { get; set; } public DateTimeOffset? ExpiredAt { get; set; } public string? PickupCode { get; set; } public DateTimeOffset? PickedUpAt { get; set; } public string? PickedUpBy { get; set; } public List<OnlineOrderLineEntity> Lines { get; set; } = []; }
public sealed class OnlineOrderLineEntity { public Guid Id { get; set; } public Guid OrderId { get; set; } public Guid ProductId { get; set; } public string ProductName { get; set; } = ""; public decimal Quantity { get; set; } public string UnitType { get; set; } = ""; public decimal UnitPrice { get; set; } public decimal LineTotal { get; set; } public decimal VatAmount { get; set; } }
public sealed class StripeCheckoutEntity { public Guid Id { get; set; } public Guid OrderId { get; set; } public string? SessionId { get; set; } public string? Url { get; set; } public decimal TndAmount { get; set; } public long AmountCents { get; set; } public decimal Rate { get; set; } public string Status { get; set; } = ""; public long Version { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class AppUserEntity { public Guid Id { get; set; } public string Username { get; set; } = ""; public string DisplayName { get; set; } = ""; }
