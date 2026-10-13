using FundLedger.Api.Hosting;
using FundLedger.Application.Sync;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FundLedger.Api.Endpoints;

/// <summary>Offline outbox ingest (Phase 5, TRD §8.3).</summary>
internal static class SyncEndpoints
{
    public static void Map(WebApplication app)
    {
        // Deliberately WITHOUT TenantTransactionFilter: every queued entry runs in its own database transaction
        // (SyncService), so one bad entry can't roll back its neighbours.
        var g = app.MapGroup("/api/v1/sync").WithTags("Sync").RequireAuthorization(Policies.User);

        g.MapPost("/transactions", async Task<Ok<SyncResponse>> (SyncRequest b, SyncService s, HttpContext h) =>
                TypedResults.Ok(await s.SyncAsync(b, h.RequestAborted).ConfigureAwait(false)))
            .WithName("SyncTransactions")
            .WithSummary("Record up to 50 entries queued on the device. Each gets CREATED, DUPLICATE, REJECTED or RETRY.")
            .ProducesProblem(400);

        g.MapPost("/discard", async Task<NoContent> (DiscardRequest b, SyncService s, HttpContext h) =>
            {
                await using var tx = await h.RequestServices.GetRequiredService<FundLedger.Infrastructure.Persistence.FundLedgerDbContext>().Database.BeginTransactionAsync(h.RequestAborted).ConfigureAwait(false);
                await s.DiscardAsync(b, h.RequestAborted).ConfigureAwait(false);
                await tx.CommitAsync(h.RequestAborted).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("DiscardOfflineEntry").WithSummary("Records that a rejected offline entry was discarded on the device.");
    }
}
