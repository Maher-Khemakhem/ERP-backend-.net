using System.Text.RegularExpressions;

namespace SupermarketErp.Api.Tests;

/// <summary>
/// Executable architecture rules for the modular monolith. All modules share one database context (a sale must
/// commit POS and Inventory changes atomically), so nothing in the compiler stops a module from reading another
/// module's tables. These tests do. They scan the source of each feature folder:
///
///   1. A module only touches the tables (DbSets) and entity classes it owns. Anything else goes through the
///      owning module's public contract (e.g. IInventoryStock, IPricingCalculator, IAuthService).
///   2. A module only depends on the modules it is explicitly allowed to (the arrows below).
///
/// To let a module depend on another, change the table below on purpose, in the same change that justifies it.
/// </summary>
public sealed partial class ModuleBoundaryTests
{
    /// <summary>Who owns which DbSet / entity. The outbox is platform infrastructure that every module may use.</summary>
    private static readonly Dictionary<string, string[]> Owned = new()
    {
        ["Auth"] = ["AppUsers|AppUserEntity"],
        ["Inventory"] = ["Products|ProductEntity", "ProductFamilies|ProductFamilyEntity", "StockLevels|StockLevelEntity",
            "LotEntries|LotEntryEntity", "Lots|LotEntity"],
        ["Pricing"] = ["ProductPrices|ProductPriceEntity", "Promotions|PromotionEntity"],
        ["Pos"] = ["Tills|TillEntity", "CashSessions|CashSessionEntity", "PosTransactions|PosTransactionEntity",
            "TransactionLines|TransactionLineEntity"],
        ["Procurement"] = ["Suppliers|SupplierEntity", "PurchaseOrders|PurchaseOrderEntity",
            "PurchaseOrderLines|PurchaseOrderLineEntity", "LowStockAlerts|LowStockAlertEntity"],
        ["Finance"] = ["JournalEntries|JournalEntryEntity", "JournalLines|JournalLineEntity", "LedgerEntries|LedgerEntryEntity",
            "SupplierInvoices|SupplierInvoiceEntity", "SupplierPayments|SupplierPaymentEntity"],
        ["Ecommerce"] = ["Carts|CartEntity", "CartItems|CartItemEntity", "OnlineOrders|OnlineOrderEntity",
            "OnlineOrderLines|OnlineOrderLineEntity", "StripeCheckouts|StripeCheckoutEntity"],
    };

    private static readonly string[] Platform = ["DotnetOutbox", "DotnetOutboxEntity"];

    /// <summary>module -> modules it may depend on.</summary>
    private static readonly Dictionary<string, string[]> MayDependOn = new()
    {
        ["Auth"] = [],
        ["Inventory"] = ["Auth"],
        ["Pricing"] = ["Auth", "Inventory"],
        ["Pos"] = ["Auth", "Inventory", "Pricing"],
        ["Ecommerce"] = ["Auth", "Inventory", "Pricing"],
        ["Procurement"] = ["Auth", "Inventory"],
        ["Finance"] = ["Auth"],
    };

    [Fact]
    public void Every_feature_folder_is_covered_by_the_rules()
    {
        var folders = Modules().Select(m => m.Name).Order().ToArray();
        Assert.Equal(MayDependOn.Keys.Order().ToArray(), folders); // a new module must be added to the tables above
    }

    [Fact]
    public void Modules_only_touch_tables_and_entities_they_own()
    {
        var owner = new Dictionary<string, string>(); // DbSet or entity name -> module
        foreach (var (module, pairs) in Owned)
            foreach (var name in pairs.SelectMany(p => p.Split('|')))
                owner[name] = module;
        var identifier = new Regex(@"\b(" + string.Join("|", owner.Keys) + @")\b");

        var violations = new List<string>();
        foreach (var (module, files) in Modules().Select(m => (m.Name, m.Files)))
            foreach (var file in files)
                foreach (var (number, line) in CodeLines(file))
                    foreach (Match match in identifier.Matches(line))
                        if (owner[match.Value] != module && !Platform.Contains(match.Value))
                            violations.Add($"{module}: {Relative(file)}:{number} uses {match.Value} (owned by {owner[match.Value]})");

        Assert.True(violations.Count == 0,
            "A module must not touch another module's tables or entities; call that module's public contract instead:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Modules_only_depend_on_the_modules_they_are_allowed_to()
    {
        var violations = new List<string>();
        foreach (var (module, files) in Modules().Select(m => (m.Name, m.Files)))
            foreach (var file in files)
                foreach (var (number, line) in CodeLines(file))
                    foreach (Match match in FeatureReference().Matches(line))
                    {
                        var target = match.Groups[1].Value;
                        if (target != module && !MayDependOn[module].Contains(target))
                            violations.Add($"{module} -> {target}: {Relative(file)}:{number}");
                    }

        Assert.True(violations.Count == 0, "Forbidden module dependencies:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void The_dependency_rules_have_no_cycles()
    {
        foreach (var start in MayDependOn.Keys)
        {
            var seen = new HashSet<string>();
            var queue = new Queue<string>(MayDependOn[start]);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                Assert.NotEqual(start, next);
                if (seen.Add(next)) foreach (var further in MayDependOn[next]) queue.Enqueue(further);
            }
        }
    }

    // ---- scanning --------------------------------------------------------------------------

    [GeneratedRegex(@"\bSupermarketErp\.Api\.Features\.(\w+)\b")]
    private static partial Regex FeatureReference();

    private static IEnumerable<(string Name, string[] Files)> Modules()
    {
        var root = FeaturesDirectory();
        return Directory.GetDirectories(root).Select(directory =>
            (Path.GetFileName(directory), Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)));
    }

    /// <summary>Code lines with line numbers, comments removed so prose mentioning a table does not count.</summary>
    private static IEnumerable<(int Number, string Text)> CodeLines(string file)
    {
        var inBlockComment = false;
        var number = 0;
        foreach (var raw in File.ReadLines(file))
        {
            number++;
            var line = raw;
            if (inBlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) continue;
                line = line[(end + 2)..];
                inBlockComment = false;
            }
            while (line.Contains("/*", StringComparison.Ordinal))
            {
                var start = line.IndexOf("/*", StringComparison.Ordinal);
                var end = line.IndexOf("*/", start + 2, StringComparison.Ordinal);
                if (end < 0) { line = line[..start]; inBlockComment = true; break; }
                line = line[..start] + line[(end + 2)..];
            }
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            if (line.Trim().Length > 0) yield return (number, line);
        }
    }

    private static string FeaturesDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "SupermarketErp.Api", "Features");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"Could not find SupermarketErp.Api/Features above {AppContext.BaseDirectory}.");
    }

    private static string Relative(string file) => file[(FeaturesDirectory().Length + 1)..];
}
