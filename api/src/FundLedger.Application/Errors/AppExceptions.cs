namespace FundLedger.Application.Errors;

// Request-level failures with a stable code (TRD §11.4). The API maps each type to its
// HTTP status; messages are safe to show to users. Standard exception constructors are
// intentionally omitted: these are only ever created with a code and message.
#pragma warning disable CA1032

public abstract class AppException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public abstract int Status { get; }
}

/// <summary>404 — also used when the caller may not know the resource exists (IDOR-safe).</summary>
public sealed class NotFoundException(string message = "Not found.") : AppException("NOT_FOUND", message)
{
    public override int Status => 404;
}

public sealed class ForbiddenException(string code = "FORBIDDEN", string message = "You don't have permission to do this.")
    : AppException(code, message)
{
    public override int Status => 403;
}

public sealed class ConflictException(string code, string message) : AppException(code, message)
{
    public override int Status => 409;
}

public sealed class UnauthenticatedException(string code = "UNAUTHENTICATED", string message = "Please sign in again.")
    : AppException(code, message)
{
    public override int Status => 401;
}

public sealed class TooManyAttemptsException(TimeSpan retryAfter)
    : AppException("ACCOUNT_LOCKED", "Too many attempts. Please try again later.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;

    public override int Status => 429;
}

public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : AppException("VALIDATION_FAILED", "Some fields need attention.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public override int Status => 400;

    public static ValidationFailedException For(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}

#pragma warning restore CA1032
