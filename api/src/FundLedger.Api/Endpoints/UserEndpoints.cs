using FundLedger.Api.Hosting;
using FundLedger.Application.Profile;
using FundLedger.Application.Users;
using FundLedger.Domain.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FundLedger.Api.Endpoints;

/// <summary><c>/api/v1/me</c>, <c>/api/v1/funds</c> (read) and Admin <c>/api/v1/users</c> (PRD §21.1).</summary>
internal static class UserEndpoints
{
    public static void Map(WebApplication app)
    {
        var me = app.MapGroup("/api/v1").WithTags("Profile").AddEndpointFilter<TenantTransactionFilter>();

        me.MapGet("/me", async Task<Ok<MeResponse>> (MeService service, HttpContext http) =>
                TypedResults.Ok(await service.GetAsync(http.RequestAborted).ConfigureAwait(false)))
            .RequireAuthorization(Policies.PinChangePending)
            .WithName("GetMe")
            .WithSummary("Current user, organization, and accessible funds with permissions.");

        me.MapGet("/funds", async Task<Ok<IReadOnlyList<AccessibleFund>>> (MeService service, HttpContext http) =>
                TypedResults.Ok(await service.ListAccessibleFundsAsync(http.RequestAborted).ConfigureAwait(false)))
            .RequireAuthorization(Policies.User)
            .WithName("ListFunds")
            .WithSummary("Funds the caller can open (Admins: all; Members: assigned Active/Closed).");

        var users = app.MapGroup("/api/v1/users").WithTags("Users")
            .RequireAuthorization(Policies.Admin)
            .AddEndpointFilter<TenantTransactionFilter>();

        users.MapGet("/", async Task<Ok<UserList>> (EnumQuery<UserStatus>? status, EnumQuery<UserRole>? role, string? q, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.ListAsync(status.Unwrap(), role.Unwrap(), q, http.RequestAborted).ConfigureAwait(false)))
            .WithName("ListUsers");

        users.MapGet("/{id:guid}", async Task<Ok<UserDetail>> (Guid id, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.GetAsync(id, http.RequestAborted).ConfigureAwait(false)))
            .WithName("GetUser").ProducesProblem(404);

        users.MapPost("/", async Task<Created<CreatedUser>> (CreateUserRequest body, UserAdminService service, HttpContext http) =>
            {
                var created = await service.CreateAsync(body, http.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/users/{created.User.Id}", created);
            })
            .Validate<CreateUserRequest>()
            .WithName("CreateUser")
            .WithSummary("Create a user. The response contains a one-time temporary PIN.")
            .ProducesProblem(409);

        users.MapPut("/{id:guid}", async Task<Ok<UserDetail>> (Guid id, UpdateUserRequest body, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.UpdateAsync(id, body, http.RequestAborted).ConfigureAwait(false)))
            .Validate<UpdateUserRequest>()
            .WithName("UpdateUser").ProducesProblem(404).ProducesProblem(409);

        users.MapPatch("/{id:guid}/status", async Task<Ok<UserDetail>> (Guid id, SetUserStatusRequest body, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.SetStatusAsync(id, body, http.RequestAborted).ConfigureAwait(false)))
            .Validate<SetUserStatusRequest>()
            .WithName("SetUserStatus")
            .WithSummary("Activate or deactivate. Deactivation signs the user out everywhere immediately.")
            .ProducesProblem(404).ProducesProblem(409);

        users.MapPut("/{id:guid}/fund-access", async Task<Ok<UserDetail>> (Guid id, SetFundAccessRequest body, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.SetFundAccessAsync(id, body, http.RequestAborted).ConfigureAwait(false)))
            .Validate<SetFundAccessRequest>()
            .WithName("SetUserFundAccess")
            .WithSummary("Replace a Member's fund access list (PRD /users/{id}/permissions).")
            .ProducesProblem(404).ProducesProblem(409);

        users.MapPost("/{id:guid}/reset-pin", async Task<Ok<PinResetResult>> (Guid id, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.ResetPinAsync(id, http.RequestAborted).ConfigureAwait(false)))
            .WithName("ResetUserPin")
            .WithSummary("Issue a new temporary PIN (shown once) and sign the user out everywhere.")
            .ProducesProblem(404).ProducesProblem(409);

        users.MapGet("/{id:guid}/sessions", async Task<Ok<IReadOnlyList<SessionInfo>>> (Guid id, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(await service.ListSessionsAsync(id, http.RequestAborted).ConfigureAwait(false)))
            .WithName("ListUserSessions").ProducesProblem(404);

        users.MapPost("/{id:guid}/sessions/revoke", async Task<Ok<RevokeSessionsResponse>> (Guid id, RevokeSessionsRequest body, UserAdminService service, HttpContext http) =>
                TypedResults.Ok(new RevokeSessionsResponse(await service.RevokeSessionsAsync(id, body, http.RequestAborted).ConfigureAwait(false))))
            .WithName("RevokeUserSessions")
            .WithSummary("Sign the user out of one session (familyId) or all sessions.")
            .ProducesProblem(404);
    }
}

public sealed record RevokeSessionsResponse(int SessionsSignedOut);
