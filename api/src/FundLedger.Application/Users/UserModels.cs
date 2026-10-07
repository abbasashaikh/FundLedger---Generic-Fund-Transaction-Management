using FluentValidation;
using FundLedger.Domain.Users;

namespace FundLedger.Application.Users;

public sealed record FundAccessGrant(
    Guid FundId,
    bool CanMoneyIn = true,
    bool CanMoneyOut = true,
    bool CanTransfer = false,
    bool CanViewReports = true,
    bool CanExport = false,
    bool CanViewAllTxns = true);

public sealed record UserListItem(
    Guid Id, string FullName, string Mobile, string? Email, UserRole Role, UserStatus Status,
    DateTimeOffset? LastLoginAt, int FundCount, bool PinMustChange);

public sealed record UserList(IReadOnlyList<UserListItem> Items, int ActiveCount, int MaxActiveUsers);

public sealed record UserDetail(
    Guid Id, string FullName, string Mobile, string? Email, UserRole Role, UserStatus Status,
    DateTimeOffset? LastLoginAt, bool PinMustChange, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt,
    IReadOnlyList<FundAccessGrant> FundAccess, uint Version);

public sealed record CreateUserRequest(
    string FullName, string Mobile, string? Email, UserRole Role, IReadOnlyList<FundAccessGrant>? FundAccess);

/// <summary>The temporary PIN is returned exactly once, for the Admin to hand over in person.</summary>
public sealed record CreatedUser(UserDetail User, string TemporaryPin);

public sealed record UpdateUserRequest(string FullName, string Mobile, string? Email, UserRole Role, uint Version);

public sealed record SetUserStatusRequest(UserStatus Status, string? Reason);

public sealed record SetFundAccessRequest(IReadOnlyList<FundAccessGrant> Items);

public sealed record PinResetResult(string TemporaryPin);

public sealed record SessionInfo(
    Guid FamilyId, string? Device, string? IpAddress, DateTimeOffset SignedInAt, DateTimeOffset? LastUsedAt,
    DateTimeOffset ExpiresAt, bool IsCurrent);

public sealed record RevokeSessionsRequest(Guid? FamilyId);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Mobile).Must(m => MobileNumber.TryNormalize(m, out _)).WithMessage("Enter a valid 10-digit mobile number.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Role).IsInEnum();
        RuleFor(x => x.FundAccess)
            .Must(a => a is null || a.Select(g => g.FundId).Distinct().Count() == a.Count)
            .WithMessage("Each fund can be listed only once.");
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Mobile).Must(m => MobileNumber.TryNormalize(m, out _)).WithMessage("Enter a valid 10-digit mobile number.");
        RuleFor(x => x.Email).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class SetUserStatusRequestValidator : AbstractValidator<SetUserStatusRequest>
{
    public SetUserStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

public sealed class SetFundAccessRequestValidator : AbstractValidator<SetFundAccessRequest>
{
    public SetFundAccessRequestValidator()
    {
        RuleFor(x => x.Items).NotNull()
            .Must(a => a.Select(g => g.FundId).Distinct().Count() == a.Count)
            .WithMessage("Each fund can be listed only once.");
    }
}
