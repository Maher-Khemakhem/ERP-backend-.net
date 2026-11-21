using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SupermarketErp.Api.Tests;

/// <summary>Many tills selling the same product at once: no server errors, no overselling, books stay balanced.</summary>
public sealed class ConcurrencyTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ErpApi Api => fixture.Api;

    [Fact]
    public async Task A_unit_of_work_chosen_as_deadlock_victim_is_run_again_and_succeeds()
    {
        await fixture.ExecuteAsync("CREATE TABLE dbo.deadlock_probe (id INT PRIMARY KEY, n INT NOT NULL); " +
                                   "INSERT dbo.deadlock_probe VALUES (1, 0), (2, 0);");
        var bothHoldOneRow = new Barrier(2);
        var attempts = 0;

        async Task Touch(int firstRow, int secondRow, bool synchronise)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SupermarketErp.Api.Infrastructure.BusinessDbContext>();
            var uow = scope.ServiceProvider.GetRequiredService<SupermarketErp.Api.Infrastructure.IUnitOfWork>();
            await uow.InTransaction(async () =>
            {
                Interlocked.Increment(ref attempts);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.deadlock_probe SET n = n + 1 WHERE id = {firstRow}");
                if (synchronise && Volatile.Read(ref attempts) <= 2) bothHoldOneRow.SignalAndWait(TimeSpan.FromSeconds(10));
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.deadlock_probe SET n = n + 1 WHERE id = {secondRow}");
            }, CancellationToken.None);
        }

        await Task.WhenAll(Task.Run(() => Touch(1, 2, true)), Task.Run(() => Touch(2, 1, true)));

        Assert.True(attempts >= 3, $"expected one of the two units of work to run again, but there were {attempts} runs in total");
        Assert.Equal(2, await fixture.ScalarAsync<int>("SELECT n FROM dbo.deadlock_probe WHERE id = 1"));
        Assert.Equal(2, await fixture.ScalarAsync<int>("SELECT n FROM dbo.deadlock_probe WHERE id = 2"));
    }

    [Fact]
    public async Task Sales_of_two_products_in_opposite_orders_all_succeed()
    {
        var first = await Api.ProductWithStock(price: 5m, quantity: 50, unitCost: 1m);
        var second = await Api.ProductWithStock(price: 5m, quantity: 50, unitCost: 1m);
        var session = await Api.OpenSession(openingFloat: 0m);
        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var ticket = await Api.StartTransaction(session);
            await Api.AddLine(ticket, i % 2 == 0 ? first : second, 1);
            await Api.AddLine(ticket, i % 2 == 0 ? second : first, 1);
            ids.Add(ticket);
        }

        var replies = await Task.WhenAll(ids.Select(id => Task.Run(async () => await Api.Complete(id, "CASH", 100m))));

        Assert.True(replies.All(r => r.Status == HttpStatusCode.OK), string.Join("; ", replies.Where(r => r.Status != HttpStatusCode.OK).Select(r => r.Body.ToString())));
        Assert.Equal(40m, await Api.Available(first));
        Assert.Equal(40m, await Api.Available(second));
    }

    [Fact]
    public async Task Parallel_sales_of_one_product_never_fail_with_a_server_error_or_oversell()
    {
        const int tickets = 16, stock = 10;
        var product = await Api.ProductWithStock(price: 5m, quantity: stock, unitCost: 1m);
        var session = await Api.OpenSession(openingFloat: 0m);
        var ids = new List<Guid>();
        for (var i = 0; i < tickets; i++)
        {
            var ticket = await Api.StartTransaction(session);
            await Api.AddLine(ticket, product, 1);
            ids.Add(ticket);
        }

        var replies = await Task.WhenAll(ids.Select(id => Task.Run(async () => await Api.Complete(id, "CASH", 100m))));
        var results = replies.Select(r => r.Status).ToArray();

        Assert.True(results.All(s => (int)s < 500), string.Join("; ",replies.Where(r => (int)r.Status >= 500).Select(r => r.Body.ToString())));
        var sold = results.Count(s => s == HttpStatusCode.OK);
        Assert.Equal(stock, sold);                                              // every unit sold exactly once
        Assert.Equal(tickets - stock, results.Count(s => s == HttpStatusCode.Conflict)); // the rest refused: out of stock
        Assert.Equal(0m, await Api.Available(product));
        foreach (var id in ids.Where((_, i) => results[i] == HttpStatusCode.OK))
            ErpApi.AssertBalanced(await Api.WaitForJournal($"sale:{id}"));
    }
}
