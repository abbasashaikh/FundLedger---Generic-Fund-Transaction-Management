using FluentValidation;
using FundLedger.Application.Errors;

namespace FundLedger.Api.Hosting;

/// <summary>
/// Validates the request body of type <typeparamref name="T"/> with its FluentValidation
/// validator before the handler runs (TRD TR-074 / Standard §3.1). Field errors are
/// returned as ProblemDetails <c>errors</c> with camelCase keys.
/// </summary>
internal sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var body = context.Arguments.OfType<T>().FirstOrDefault()
            ?? throw ValidationFailedException.For("body", "A request body is required.");

        var result = await validator.ValidateAsync(body, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new ValidationFailedException(result.Errors
                .GroupBy(e => char.ToLowerInvariant(e.PropertyName[0]) + e.PropertyName[1..])
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray()));
        }

        return await next(context).ConfigureAwait(false);
    }
}

internal static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
