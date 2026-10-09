namespace FundLedger.Domain;

/// <summary>
/// A business-rule violation. <see cref="Code"/> is the stable error code returned
/// to clients in ProblemDetails (TRD §11.4); the message is safe to show to users.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message) => Code = code;

    public DomainException()
        : this("DOMAIN_ERROR", "A business rule was violated.")
    {
    }

    public DomainException(string message)
        : this("DOMAIN_ERROR", message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException) => Code = "DOMAIN_ERROR";

    public string Code { get; }
}
