namespace SupermarketErp.Api.Features.Auth;

public static class Roles
{
    public const string Admin = "ADMIN";
    public const string Manager = "MANAGER";
    public const string Cashier = "CASHIER";
    public const string InventoryManager = "INVENTORY_MANAGER";
    public const string Accountant = "ACCOUNTANT";
    public const string Customer = "CUSTOMER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Admin, Manager, Cashier, InventoryManager, Accountant, Customer
    };
}

public static class Policies
{
    public const string Admin = "Admin";
    public const string StaffRead = "StaffRead";
    public const string InventoryWrite = "InventoryWrite";
}
