using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SupermarketErp.Api.Features.Auth;

namespace SupermarketErp.Api.Features.Pos;

[ApiController]
[Route("api/pos")]
public sealed class PosController(
    IPosService service,
    ScannerPairingService scanner) : ControllerBase
{
    [HttpGet("tills")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<IReadOnlyList<TillView>>> ListTills(CancellationToken ct) =>
        Ok(await service.ListTills(ct));

    [HttpGet("tills/{id:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<TillView>> GetTill(Guid id, CancellationToken ct)
    {
        var till = await service.GetTill(id, ct);
        return till is null ? NotFound() : Ok(till);
    }

    [HttpPost("tills")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> CreateTill(
        [FromBody] CreateTillRequest request,
        CancellationToken ct) =>
        Ok(new { id = await service.CreateTill(request, ct) });

    [HttpPost("sessions/open")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> OpenSession(
        [FromBody] OpenSessionRequest request,
        CancellationToken ct) =>
        Ok(new { cashSessionId = await service.OpenSession(request, ct) });

    [HttpGet("sessions/active")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<IActionResult> GetActiveSession(Guid tillId, CancellationToken ct)
    {
        var session = await service.GetActiveSession(tillId, ct);
        return session is null ? NoContent() : Ok(session);
    }

    [HttpGet("sessions/{cashSessionId:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<SessionView>> GetSession(
        Guid cashSessionId,
        CancellationToken ct) =>
        Ok(await service.GetSession(cashSessionId, ct));

    [HttpPost("sessions/{cashSessionId:guid}/close")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> CloseSession(
        Guid cashSessionId,
        [FromBody] CloseSessionRequest request,
        CancellationToken ct)
    {
        await service.CloseSession(cashSessionId, request, ct);
        return Ok();
    }

    [HttpGet("transactions")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<IReadOnlyList<TransactionView>>> ListTransactions(
        CancellationToken ct) =>
        Ok(await service.ListTransactions(ct));

    [HttpPost("transactions/start")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> StartTransaction(
        [FromBody] StartTransactionRequest request,
        CancellationToken ct) =>
        Ok(new { transactionId = await service.StartTransaction(request, ct) });

    [HttpGet("transactions/{id:guid}")]
    [Authorize(Policy = Policies.StaffRead)]
    public async Task<ActionResult<TransactionView>> GetTransaction(
        Guid id,
        CancellationToken ct) =>
        Ok(await service.GetTransaction(id, ct));

    [HttpPost("transactions/{id:guid}/lines")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<ActionResult<AddLineResponse>> AddLine(
        Guid id,
        [FromBody] AddLineRequest request,
        CancellationToken ct) =>
        Ok(await service.AddLine(id, request, ct));

    [HttpDelete("transactions/{id:guid}/lines/{lineId:guid}")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> RemoveLine(
        Guid id,
        Guid lineId,
        CancellationToken ct)
    {
        if (!await service.RemoveLine(id, lineId, ct))
            throw new ArgumentException($"Line not found: {lineId}");
        return Ok();
    }

    [HttpPost("transactions/{id:guid}/complete")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> Complete(
        Guid id,
        [FromBody] CompleteTransactionRequest request,
        CancellationToken ct)
    {
        await service.Complete(id, request, ct);
        return Ok();
    }

    [HttpPost("transactions/{id:guid}/void")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> Void(
        Guid id,
        [FromBody] VoidTransactionRequest request,
        CancellationToken ct)
    {
        await service.Void(id, request, ct);
        return Ok();
    }

    [HttpDelete("transactions/{id:guid}")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<IActionResult> DeleteTransaction(Guid id, CancellationToken ct)
    {
        await service.DeleteTransaction(id, ct);
        return Ok();
    }

    [HttpPost("transactions/{id:guid}/scanner-session")]
    [Authorize(Roles = Roles.Admin + "," + Roles.Manager + "," + Roles.Cashier)]
    public async Task<ActionResult<ScannerPairingView>> CreateScannerSession(
        Guid id,
        CancellationToken ct) =>
        Ok(await scanner.Create(id, ct));

    [AllowAnonymous]
    [HttpPost("scanner-sessions/{sessionId:guid}/scans")]
    public async Task<ActionResult<ScannerScanResult>> Scan(
        Guid sessionId,
        [FromBody] ScannerScanRequest request,
        CancellationToken ct) =>
        Ok(await scanner.Scan(
            sessionId,
            Request.Headers["X-Scanner-Token"].FirstOrDefault(),
            request.Barcode,
            ct));

    [AllowAnonymous]
    [HttpDelete("scanner-sessions/{sessionId:guid}")]
    public IActionResult CloseScannerSession(Guid sessionId)
    {
        scanner.Close(sessionId, Request.Headers["X-Scanner-Token"].FirstOrDefault());
        return NoContent();
    }
}
