namespace SupermarketErp.Api.Shared;

/// <summary>
/// A business rule or input check failed, as opposed to a bug. Only these exceptions are shown to API callers
/// with their message; anything else is an unexpected failure and is reported as a server error.
/// </summary>
public abstract class DomainException(string message) : Exception(message);

/// <summary>The request is malformed or breaks a rule on its own: bad input, unknown reference. HTTP 400.</summary>
public sealed class DomainValidationException(string message) : DomainException(message);

/// <summary>The request is fine but clashes with the current state: wrong status, not enough stock, a duplicate. HTTP 409.</summary>
public sealed class ConflictException(string message) : DomainException(message);
