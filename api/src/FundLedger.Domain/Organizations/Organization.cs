namespace FundLedger.Domain.Organizations;

/// <summary>The tenant (PRD §6.1). Maps to <c>fl.organizations</c>.</summary>
public sealed class Organization
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public required string ShortCode { get; init; }

    public string? ContactMobile { get; set; }

    public string? ContactEmail { get; set; }

    public string? Address { get; set; }

    public string? RegistrationNumber { get; set; }

    public string CurrencyCode { get; set; } = "INR";

    public string Timezone { get; set; } = "Asia/Kolkata";

    public string DateFormat { get; set; } = "dd-MMM-yyyy";

    public short MaxActiveUsers { get; set; } = 50;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
