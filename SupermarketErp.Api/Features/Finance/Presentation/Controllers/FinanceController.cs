using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Finance;

[ApiController]
[Route("api/finance")]
[Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Accountant)]
public sealed class FinanceController(IFinanceService service) : ControllerBase
{
    [HttpGet("ledger")]
    public async Task<ActionResult<IReadOnlyList<LedgerEntryView>>> ListLedger(
        CancellationToken ct) =>
        Ok(await service.ListLedger(ct));

    [HttpGet("journals")]
    public async Task<ActionResult<IReadOnlyList<JournalView>>> ListJournals(
        CancellationToken ct) =>
        Ok(await service.ListJournals(ct));

    [HttpGet("reports/profit-and-loss")]
    public async Task<ActionResult<ProfitAndLossView>> ProfitAndLoss(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct) =>
        Ok(await service.ProfitAndLoss(from, to, ct));

    [HttpGet("supplier-invoices")]
    public async Task<ActionResult<IReadOnlyList<InvoiceView>>> ListInvoices(
        CancellationToken ct) =>
        Ok(await service.ListInvoices(ct));

    [HttpPost("supplier-invoices")]
    public async Task<ActionResult<InvoiceView>> CreateInvoice(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken ct) =>
        Ok(await service.CreateInvoice(request, ct));

    [HttpPost("supplier-invoices/{invoiceId:guid}/payments")]
    public async Task<ActionResult<InvoiceView>> PayInvoice(
        Guid invoiceId,
        [FromBody] PayInvoiceRequest request,
        CancellationToken ct)
    {
        var invoice = await service.PayInvoice(invoiceId, request, ct);
        return invoice is null
            ? throw new ArgumentException($"Supplier invoice not found: {invoiceId}")
            : Ok(invoice);
    }

    [HttpPost("settlements/card")]
    public async Task<IActionResult> SettleCard(
        [FromBody] CardSettlementRequest request,
        CancellationToken ct)
    {
        await service.SettleCard(request, ct);
        return Ok();
    }
}
