namespace Wisper.Api.Leases;

/// <summary>
/// The consumer lease surface behind <c>POST/GET/DELETE /v1/leases</c> (docs/API.md §5). It validates a
/// requested lease against the host's priced allow-list, runs the wallet gate (docs/DATA_MODEL.md §8),
/// drives the host over the tunnel via <see cref="Wisper.Api.Tunnel.ITunnelRelay"/>, and persists the
/// <c>leases</c> row (docs/DATA_MODEL.md §5). Reads are strictly caller-scoped — a lease owned by another
/// user is indistinguishable from a missing one (docs/API.md §3, ownership → 404). Transport concerns
/// (auth, the <c>Idempotency-Key</c> replay) live at the endpoint; this layer is unit-testable with a
/// fake relay and in-memory repositories.
/// </summary>
public interface ILeaseService
{
    /// <summary>
    /// Validates <paramref name="request"/> against the priced image, places the wallet hold, creates the
    /// lease over the tunnel, and persists the row. Throws <see cref="Infrastructure.ApiException"/> for a
    /// bad request (<c>validation_error</c>), an image not in the host's allow-list
    /// (<c>image_not_allowed</c>), an unaffordable hold (<c>insufficient_funds</c>), or a tunnel failure
    /// (<c>host_offline</c> / <c>upstream_timeout</c> / <c>lease_failed</c>).
    /// </summary>
    Task<LeaseCreationResult> CreateAsync(
        Guid consumerUserId, CreateLeaseRequest request, CancellationToken ct = default);

    /// <summary>The caller's leases, newest first, filtered by status and cursor-paginated (docs/API.md §10).</summary>
    Task<LeasePage> ListAsync(Guid consumerUserId, LeaseListQuery query, CancellationToken ct = default);

    /// <summary>The caller's lease by id, or <c>null</c> if there is no such lease the caller can see.</summary>
    Task<LeaseView?> GetAsync(Guid consumerUserId, Guid leaseId, CancellationToken ct = default);

    /// <summary>
    /// Releases the caller's lease: sends <c>lease.release</c> over the tunnel and marks the row
    /// <c>ended</c> (<c>released</c>). Idempotent and safe to retry — an already-ended lease returns
    /// unchanged, and a host with no live tunnel is treated as already released. Returns <c>null</c> if
    /// there is no such lease the caller can see.
    /// </summary>
    Task<LeaseView?> ReleaseAsync(Guid consumerUserId, Guid leaseId, CancellationToken ct = default);
}
