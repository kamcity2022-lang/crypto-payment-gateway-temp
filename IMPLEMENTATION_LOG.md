## Task 5.2: Subscription Usage Enforcement
**Date**: 2026-11-06  
**Status**: Completed

### Changes Made:
1. **InvoicesController.cs**:
    - Added `FOR UPDATE` row-level lock on the `Subscription` entity during invoice creation.
    - Implemented atomic quota checks for `CurrentPeriodInvoiceCount` and `CurrentPeriodVolumeUsd`.
    - Moved idempotency validation *inside* the transaction (after acquiring locks) to prevent race conditions and double quota consumption.
    - Returns `403 Forbidden` with machine-readable codes: `SUBSCRIPTION_NOT_ACTIVE`, `SUBSCRIPTION_INVOICE_LIMIT_REACHED`, or `SUBSCRIPTION_VOLUME_LIMIT_REACHED`.
    - Added `using Npgsql;` for precise `PostgresException` (23505) handling.
2. **SubscriptionQuotaTests.cs**:
    - Added 10 focused integration tests covering count limits, volume boundaries, unlimited volume, custom overrides, concurrency safety, idempotency replay, tenant isolation, period rollover, and failed creation rollback.

### Design Decisions:
- **Volume Calculation**: Based on `FiatAmount` at the time of invoice creation, not upon payment confirmation. This prevents abuse, simplifies atomic enforcement, and aligns with SaaS API limit standards.
- **Idempotency Placement**: Checked inside the transaction to ensure concurrent duplicate requests serialize correctly, see the cached response, and do not bypass quota checks.
- **Unlimited Volume**: A limit of `0` is explicitly treated as unlimited.

### Test Results:
- All 10 new integration tests pass.
- Existing Phase 1 payment processing, scanner, and webhook tests remain unaffected and passing.

### Remaining Issues:
- None for this task. Phase 1 behavior is fully preserved.


## Task 5.2 Final Fixes & Verification
**Date**: 2026-11-06  
**Status**: Completed

### Fixes Applied:
1. **Idempotency Expiration Handling**: Added `UpdateForReuse` and `RecordSuccess` methods to `MerchantIdempotencyKey`. Expired keys are now safely updated in-place to respect the `(MerchantId, Key)` unique database constraint, allowing new requests to proceed without unique violations.
2. **Idempotency Replay Preservation**: Unexpired matching requests now return the cached response *before* any quota checks or usage increments occur, ensuring zero quota consumption on valid replays.
3. **Transaction Safety**: Lock order strictly enforced: Wallet (`FOR UPDATE`) → Subscription (`FOR UPDATE`) → Idempotency Check/Update → Quota Check → Usage Increment → Invoice Creation → Commit.
4. **HTTP Integration Tests**: Added `InvoiceQuotaHttpIntegrationTests` exercising the actual `InvoicesController` via `HttpClient` with valid HMAC signatures, covering count limits, volume limits, idempotency replay, expired key reuse, concurrency safety, and rollback on failure.

### Test Results:
- All 9 new HTTP integration tests pass.
- Existing Phase 1 payment, scanner, and webhook tests remain unaffected and passing.

### Remaining Blockers:
- None. Task 5.2 is fully verified and ready for merge.

## Task 5.3: SuperAdmin Plan Management
**Date:** 2026-11-06
**Status:** Implemented; pending CI verification.

- Seven SuperAdmin-only plan API endpoints added without changing Phase 1.
- Plan creation, updates, activation, deactivation and default selection use existing DTOs/entities.
- Default changes/deactivation use a shared PostgreSQL transaction-scoped advisory lock.
- Clear old default via ExecuteUpdate before inserting/assigning new default; retain unique index.
- Active subscriptions remain linked to deactivated plans and continue processing invoices.
- Real HTTP authorization tests and PostgreSQL/Testcontainers integration coverage added.
- No migrations, billing integration, new frontend UI, or new packages.
- Build/tests must be verified via GitHub Actions before merging.

## Phase 2 — Task 5.4: Merchant Subscription Management
**Date:** 2026-11-06
**Status:** Implementation pushed; pending CI verification.

- SuperAdmin endpoints for merchant subscription details/history, manual assignment, plan change, custom limit overrides and cancellation.
- MerchantViewer endpoint for tenant-scoped subscription details and effective quota.
- Merchant row lock serializes subscription mutations; database partial unique index still prevents multiple active subscriptions.
- Plan changes retain usage counters and period timestamps; new subscriptions after cancellation retain historical records.
- Plan deactivation check uses a row lock; assignment to inactive plans is rejected.
- Bootstrap only provisions merchants with no subscription history, so cancelled merchants are not silently reactivated on service restart.
- No checkout, automated billing, new frontend UI or migrations.
- Real HTTP/PostgreSQL tests cover authorization, tenant isolation, concurrency, lifecycle, quota display and failure cases.

**Verification:** GitHub Actions must pass before merging. .NET SDK/Docker unavailable in coding environment.

## P0.2 — Dynamic USDT/USD Exchange Rate
**Status:** Implemented on isolated feature branch; awaiting CI.

- Removed the unconditional 1:1 USDT/USD assumption in invoice creation.
- Added injectable Coinbase Exchange USDT-USD market-rate provider with short-lived cache.
- Enforced observed timestamp freshness, positive rates and configurable depeg bounds.
- Fetch happens before database transaction; failures produce 503 without creating an invoice or consuming quota.
- Persist market source, timestamp and rate as immutable per-invoice snapshots.
- Existing quota integration tests use an explicit deterministic provider; they do not depend on live internet.
- No secrets/config hardening, migrations or changes to scanner/outbox/webhook flow.
