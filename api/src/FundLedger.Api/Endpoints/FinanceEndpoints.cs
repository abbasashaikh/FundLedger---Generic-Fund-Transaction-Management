using FundLedger.Api.Hosting;
using FundLedger.Application.Funds;
using FundLedger.Application.Ledger;
using FundLedger.Application.Lookups;
using FundLedger.Domain.Ledger;
using FundLedger.Domain.Lookups;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FundLedger.Api.Endpoints;

/// <summary>Funds, master data, transactions, balances and the dashboard (Phase 2).</summary>
internal static class FinanceEndpoints
{
    public static void Map(WebApplication app)
    {
        MapFunds(app);
        MapLookups(app);
        MapLedger(app);
    }

    private static void MapFunds(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/funds").WithTags("Funds").RequireAuthorization(Policies.Admin).AddEndpointFilter<TenantTransactionFilter>();

        g.MapGet("/manage", async Task<Ok<IReadOnlyList<FundSummary>>> (FundService s, HttpContext h) =>
            TypedResults.Ok(await s.ListAsync(h.RequestAborted).ConfigureAwait(false))).WithName("ManageFunds")
            .WithSummary("All funds with type, status and balance (Admin).");

        g.MapGet("/{id:guid}", async Task<Ok<FundDetail>> (Guid id, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.GetAsync(id, h.RequestAborted).ConfigureAwait(false))).WithName("GetFund").ProducesProblem(404);

        g.MapPost("/", async Task<Created<FundDetail>> (SaveFundRequest b, FundService s, HttpContext h) =>
            {
                var d = await s.CreateAsync(b, h.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/funds/{d.Fund.Id}", d);
            }).Validate<SaveFundRequest>().WithName("CreateFund").ProducesProblem(409);

        g.MapPut("/{id:guid}", async Task<Ok<FundDetail>> (Guid id, SaveFundRequest b, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.UpdateAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<SaveFundRequest>().WithName("UpdateFund").ProducesProblem(404).ProducesProblem(409);

        g.MapPost("/{id:guid}/activate", async Task<Ok<FundDetail>> (Guid id, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.ActivateAsync(id, h.RequestAborted).ConfigureAwait(false))).WithName("ActivateFund").ProducesProblem(409);

        g.MapPost("/{id:guid}/close", async Task<Ok<FundDetail>> (Guid id, FundReasonRequest b, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.CloseAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<FundReasonRequest>().WithName("CloseFund").WithSummary("Close a fund: no new transactions until reopened.").ProducesProblem(409);

        g.MapPost("/{id:guid}/reopen", async Task<Ok<FundDetail>> (Guid id, FundReasonRequest b, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.ReopenAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<FundReasonRequest>().WithName("ReopenFund").ProducesProblem(400).ProducesProblem(409);

        g.MapPost("/{id:guid}/archive", async Task<Ok<FundDetail>> (Guid id, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.ArchiveAsync(id, h.RequestAborted).ConfigureAwait(false))).WithName("ArchiveFund").ProducesProblem(409);

        g.MapGet("/{id:guid}/opening-balances", async Task<Ok<IReadOnlyList<OpeningBalanceDto>>> (Guid id, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.GetOpeningBalancesAsync(id, h.RequestAborted).ConfigureAwait(false))).WithName("GetOpeningBalances");

        g.MapPut("/{id:guid}/opening-balances", async Task<Ok<IReadOnlyList<OpeningBalanceDto>>> (Guid id, SetOpeningBalancesRequest b, FundService s, HttpContext h) =>
            TypedResults.Ok(await s.SetOpeningBalancesAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<SetOpeningBalancesRequest>().WithName("SetOpeningBalances")
            .WithSummary("Set opening balances per account (Admin; a reason is required on an active fund).").ProducesProblem(400).ProducesProblem(409);
    }

    private static void MapLookups(WebApplication app)
    {
        var read = app.MapGroup("/api/v1").WithTags("Master data").RequireAuthorization(Policies.User).AddEndpointFilter<TenantTransactionFilter>();
        var write = app.MapGroup("/api/v1").WithTags("Master data").RequireAuthorization(Policies.Admin).AddEndpointFilter<TenantTransactionFilter>();

        read.MapGet("/fund-types", async Task<Ok<IReadOnlyList<FundTypeDto>>> (bool? includeInactive, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.ListFundTypesAsync(includeInactive == true, h.RequestAborted).ConfigureAwait(false))).WithName("ListFundTypes");
        write.MapPost("/fund-types", async Task<Created<FundTypeDto>> (UpsertFundTypeRequest b, LookupService s, HttpContext h) =>
            {
                var d = await s.CreateFundTypeAsync(b, h.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/fund-types/{d.Id}", d);
            }).Validate<UpsertFundTypeRequest>().WithName("CreateFundType").ProducesProblem(409);
        write.MapPut("/fund-types/{id:guid}", async Task<Ok<FundTypeDto>> (Guid id, UpsertFundTypeRequest b, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.UpdateFundTypeAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<UpsertFundTypeRequest>().WithName("UpdateFundType").ProducesProblem(404).ProducesProblem(409);

        read.MapGet("/payment-modes", async Task<Ok<IReadOnlyList<PaymentModeDto>>> (bool? includeInactive, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.ListPaymentModesAsync(includeInactive == true, h.RequestAborted).ConfigureAwait(false))).WithName("ListPaymentModes");
        write.MapPost("/payment-modes", async Task<Created<PaymentModeDto>> (UpsertPaymentModeRequest b, LookupService s, HttpContext h) =>
            {
                var d = await s.CreatePaymentModeAsync(b, h.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/payment-modes/{d.Id}", d);
            }).Validate<UpsertPaymentModeRequest>().WithName("CreatePaymentMode").ProducesProblem(409);
        write.MapPut("/payment-modes/{id:guid}", async Task<Ok<PaymentModeDto>> (Guid id, UpsertPaymentModeRequest b, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.UpdatePaymentModeAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<UpsertPaymentModeRequest>().WithName("UpdatePaymentMode").ProducesProblem(404).ProducesProblem(409);

        read.MapGet("/accounts", async Task<Ok<IReadOnlyList<AccountDto>>> (bool? includeInactive, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.ListAccountsAsync(includeInactive == true, h.RequestAborted).ConfigureAwait(false))).WithName("ListAccounts");
        write.MapPost("/accounts", async Task<Created<AccountDto>> (UpsertAccountRequest b, LookupService s, HttpContext h) =>
            {
                var d = await s.CreateAccountAsync(b, h.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/accounts/{d.Id}", d);
            }).Validate<UpsertAccountRequest>().WithName("CreateAccount").ProducesProblem(409);
        write.MapPut("/accounts/{id:guid}", async Task<Ok<AccountDto>> (Guid id, UpsertAccountRequest b, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.UpdateAccountAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<UpsertAccountRequest>().WithName("UpdateAccount").ProducesProblem(404).ProducesProblem(409);

        read.MapGet("/categories", async Task<Ok<IReadOnlyList<CategoryDto>>> (EnumQuery<CategoryDirection>? direction, Guid? fundId, bool? includeInactive, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.ListCategoriesAsync(direction.Unwrap(), fundId, includeInactive == true, h.RequestAborted).ConfigureAwait(false)))
            .WithName("ListCategories").WithSummary("Categories for a direction and fund (org-wide plus fund-specific).");
        write.MapPost("/categories", async Task<Created<CategoryDto>> (CreateCategoryRequest b, LookupService s, HttpContext h) =>
            {
                var d = await s.CreateCategoryAsync(b, h.RequestAborted).ConfigureAwait(false);
                return TypedResults.Created($"/api/v1/categories/{d.Id}", d);
            }).Validate<CreateCategoryRequest>().WithName("CreateCategory").ProducesProblem(409);
        write.MapPut("/categories/{id:guid}", async Task<Ok<CategoryDto>> (Guid id, UpdateCategoryRequest b, LookupService s, HttpContext h) =>
            TypedResults.Ok(await s.UpdateCategoryAsync(id, b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<UpdateCategoryRequest>().WithName("UpdateCategory").ProducesProblem(404).ProducesProblem(409);
    }

    private static void MapLedger(WebApplication app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Ledger").RequireAuthorization(Policies.User).AddEndpointFilter<TenantTransactionFilter>();

        g.MapPost("/transactions/deposit", async Task<Results<Created<TransactionResult>, Ok<TransactionResult>>> (CreateDepositRequest b, LedgerService s, HttpContext h) =>
                Reply(await s.CreateDepositAsync(b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<CreateDepositRequest>().WithName("CreateDeposit").WithSummary("Record Money In.").ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        g.MapPost("/transactions/expense", async Task<Results<Created<TransactionResult>, Ok<TransactionResult>>> (CreateExpenseRequest b, LedgerService s, HttpContext h) =>
                Reply(await s.CreateExpenseAsync(b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<CreateExpenseRequest>().WithName("CreateExpense").WithSummary("Record Money Out.").ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        g.MapPost("/transactions/transfer", async Task<Results<Created<TransactionResult>, Ok<TransactionResult>>> (CreateTransferRequest b, LedgerService s, HttpContext h) =>
                Reply(await s.CreateTransferAsync(b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<CreateTransferRequest>().WithName("CreateTransfer").WithSummary("Move money between two accounts of one fund.").ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        g.MapGet("/transactions", async Task<Ok<TransactionPage>> (
                Guid fundId, DateOnly? from, DateOnly? to, EnumQuery<TxnType>? type, Guid? categoryId, Guid? accountId, Guid? userId,
                Guid? paymentModeId, EnumQuery<TxnStatus>? status, string? q, EnumQuery<TxnSort>? sort, int? limit, string? cursor, LedgerService s, HttpContext h) =>
                TypedResults.Ok(await s.ListAsync(new TransactionQuery(fundId, from, to, type.Unwrap(), categoryId, accountId, userId, paymentModeId, status.Unwrap(), q,
                    sort.Unwrap() ?? TxnSort.Newest, limit, cursor), h.RequestAborted).ConfigureAwait(false)))
            .WithName("ListTransactions").WithSummary("Ledger with search, filters, sort, totals and cursor paging.").ProducesProblem(404);

        g.MapGet("/transactions/{id:guid}", async Task<Ok<TransactionDetail>> (Guid id, LedgerService s, HttpContext h) =>
            {
                var d = await s.GetAsync(id, h.RequestAborted).ConfigureAwait(false);
                ETag(h, d.Transaction.Revision);
                return TypedResults.Ok(d);
            })
            .WithName("GetTransaction").WithSummary("One entry, with what the caller may do with it. ETag is the revision.").ProducesProblem(404);

        g.MapPut("/transactions/{id:guid}", async Task<Ok<TransactionResult>> (Guid id, UpdateTransactionRequest b, [FromHeader(Name = "If-Match")] string? ifMatch, LedgerService s, HttpContext h) =>
            {
                var r = await s.UpdateAsync(id, ParseRevision(ifMatch), b, h.RequestAborted).ConfigureAwait(false);
                ETag(h, r.Transaction.Revision);
                return TypedResults.Ok(r);
            })
            .Validate<UpdateTransactionRequest>().WithName("UpdateTransaction")
            .WithSummary("Edit an entry. Needs If-Match with the revision being edited; Admin edits need a reason.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);

        g.MapPost("/transactions/{id:guid}/cancel", async Task<Ok<TransactionResult>> (Guid id, CancelTransactionRequest b, [FromHeader(Name = "If-Match")] string? ifMatch, LedgerService s, HttpContext h) =>
            {
                var r = await s.CancelAsync(id, ParseRevision(ifMatch), b, h.RequestAborted).ConfigureAwait(false);
                ETag(h, r.Transaction.Revision);
                return TypedResults.Ok(r);
            })
            .Validate<CancelTransactionRequest>().WithName("CancelTransaction")
            .WithSummary("Cancel an entry (Admin). It stays in history and leaves the balances.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);

        g.MapGet("/transactions/{id:guid}/history", async Task<Ok<IReadOnlyList<HistoryEntry>>> (Guid id, LedgerService s, HttpContext h) =>
                TypedResults.Ok(await s.HistoryAsync(id, h.RequestAborted).ConfigureAwait(false)))
            .WithName("GetTransactionHistory").WithSummary("Who changed what, when and why (newest first).").ProducesProblem(404);

        app.MapGroup("/api/v1").WithTags("Ledger").RequireAuthorization(Policies.Admin).AddEndpointFilter<TenantTransactionFilter>()
            .MapPost("/transactions/adjustment", async Task<Results<Created<TransactionResult>, Ok<TransactionResult>>> (CreateAdjustmentRequest b, LedgerService s, HttpContext h) =>
                Reply(await s.CreateAdjustmentAsync(b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<CreateAdjustmentRequest>().WithName("CreateAdjustment")
            .WithSummary("Record a correction to an account balance (Admin only; reason required).")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        g.MapGet("/dashboard", async Task<Ok<DashboardDto>> (Guid fundId, LedgerService s, HttpContext h) =>
                TypedResults.Ok(await s.DashboardAsync(fundId, h.RequestAborted).ConfigureAwait(false)))
            .WithName("GetDashboard").ProducesProblem(404);

        g.MapGet("/accounts/balances", async Task<Ok<IReadOnlyList<AccountBalanceDto>>> (Guid fundId, LedgerService s, HttpContext h) =>
                TypedResults.Ok(await s.AccountBalancesAsync(fundId, h.RequestAborted).ConfigureAwait(false)))
            .WithName("GetAccountBalances").WithSummary("Computed balance of every account within one fund.").ProducesProblem(404);
    }

    private static void ETag(HttpContext h, int revision) => h.Response.Headers.ETag = $"\"{revision}\"";

    /// <summary>Reads <c>If-Match: "3"</c> (quotes optional). Missing or malformed yields null, which the service answers with 428.</summary>
    private static int? ParseRevision(string? ifMatch) =>
        int.TryParse(ifMatch?.Trim().Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>201 for a new entry; 200 with the existing entry for an idempotent retry.</summary>
    private static Results<Created<TransactionResult>, Ok<TransactionResult>> Reply(TransactionResult r) =>
        r.Duplicate
            ? TypedResults.Ok(r)
            : TypedResults.Created($"/api/v1/transactions/{r.Transaction.Id}", r);
}
