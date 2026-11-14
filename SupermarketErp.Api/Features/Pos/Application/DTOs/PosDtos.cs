namespace SupermarketErp.Api.Features.Pos;

public sealed record CreateTillRequest(string TillNumber);
public sealed record OpenSessionRequest(Guid TillId, string CashierName, decimal OpeningFloat);
public sealed record CloseSessionRequest(decimal CountedCash);
public sealed record StartTransactionRequest(Guid CashSessionId);
public sealed record AddLineRequest(Guid ProductId, decimal QuantityValue, string UnitType);
public sealed record AddLineResponse(string LinePrice, string NetPrice, string VatAmount, string LineId);
public sealed record ScannerPairingView(Guid SessionId, string Token, string ExpiresAt);
public sealed record ScannerScanRequest(string Barcode);
public sealed record ScannerScanResult(
    string Barcode,
    Guid ProductId,
    string Sku,
    string ProductName,
    string UnitType,
    string Quantity,
    string LinePrice,
    string NetPrice,
    string VatAmount);
public sealed record CompleteTransactionRequest(string PaymentMethod, decimal? AmountReceived);
public sealed record VoidTransactionRequest(string? Reason);
public sealed record TillView(
    Guid Id,
    string TillNumber,
    string Status,
    string? CashierName,
    string CashInDrawer,
    string? OpenedAt,
    string? ClosedAt);
public sealed record SessionView(Guid Id, Guid TillId, string Status, string ExpectedCash);
public sealed record PosLineView(
    Guid Id,
    Guid ProductId,
    string Quantity,
    string LineTotal,
    string NetTotal,
    string VatAmount);
public sealed record TransactionView(
    Guid Id,
    Guid CashSessionId,
    string Status,
    string Total,
    string NetTotal,
    string VatTotal,
    string? PaymentMethod,
    string StartedAt,
    string? CompletedAt,
    IReadOnlyList<PosLineView> Lines);
