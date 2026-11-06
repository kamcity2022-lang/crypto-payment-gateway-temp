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

## P0.3 — Payment Security Verification: HMAC Body Integrity
**Status:** Draft PR, pending full CI.

- Root-cause fix: merchant HMAC attribute was an MVC action filter, which runs after body model binding and may hash a consumed/empty stream.
- Moved authentication to a resource filter before model binding.
- Compute SHA-256 of exact raw request body bytes, reset the buffered body for MVC.
- Bound signed request bodies to 1 MiB, checked Unix timestamp without overflow, decoded HMAC hex before constant-time comparison.
- Preserve existing canonical string (method, raw path/query, timestamp, nonce, raw-byte body hash) and DB nonce uniqueness.
- Real HTTP/PostgreSQL integration tests: valid body signature, empty-body signature bypass attempt, body/query tampering, replayed nonce, extreme timestamp and oversized body.
- Existing scanner, finality, outbox and webhook E2E tests remain covered by CI. No unrelated changes.

## P0.3b — Atomic Blockchain Scanner Recovery
**Status:** Draft PR pending CI.

- Transaction-scoped PostgreSQL advisory lock serializes scanner cycles across replicas.
- Transfers, invoice state, outbox events and cursor updates commit together.
- Abort and roll back a scan if TronGrid API responds with an error, receipt verification is unavailable, or the transaction is not yet solidified.
- Permanently invalid solidified receipt/log entries remain ignored.
- Reject receipts whose block number is beyond the current solidified head.
- Clear EF tracking after a rolled-back cycle to prevent dirty entities surviving the next loop.
- Retains existing idempotency unique index and webhook behavior.
- Follow-up operational task: monitor scan lag/provider outages and review transaction duration under production load.

## P0.3c — Webhook Lease Fencing & Safe Failure Details
**Status:** Draft; pending CI and integration verification.

- Previous dispatcher could overwrite the result of another worker after its 30-second lease had expired.
- Persist outcomes using a conditional, atomic PostgreSQL update matching the exact acquired lease token; stale workers cannot clobber another claim.
- Detach the claimed EF entity before finalization to avoid hidden, stale tracked state.
- Preserve at-least-once delivery with stable Event ID, payload and signing contract. Merchants still must deduplicate events by Event ID.
- Distinguish provider timeout from host cancellation. Do not persist full transport exception messages that could contain sensitive data.
- Keep HTTPS-only webhook destinations and existing retry/dead-letter/manual-redelivery behavior.
- No new schema changes, billing, admin UI or secret configuration changes.

## P0.3d — Invoice Status / Webhook Consistency
**Status:** Implemented on separate PR, pending CI.

- Hosted Checkout is a read-only projection; it never persists a payment/expiration transition.
- The Scanner Worker now persists Pending -> Expired and PartiallyPaid -> Underpaid time transitions and their signed Outbox events in the same DB transaction.
- Time transition evaluation occurs only after successfully scanning the address, preventing phantom expiration when a late payment exists on chain.
- Paid and Overpaid invoices continue to be scanned within the configured overpayment retention window to account for later transfers.
- Shared webhook event constructor ensures consistent status payload and fails the transaction if Merchant data is missing.
- Tests verify outbox parity, no duplicate expiry events, recovery, late payments, and continuing overpayment processing.

## Phase 2 — Merchant Dashboard Subscription & Current Usage
**Status:** Draft PR pending CI.

- Merchant dashboard now shows effective Plan, Subscription status, invoice and USD volume usage, remaining limits, and next usage reset.
- Uses authenticated tenant-scoped GET /api/v1/dashboard/subscription with no client-provided MerchantId.
- Allows a read-only refresh, shows network/404 failures and clears merchant-specific data upon logout.
- Uses DOM textContent rather than HTML for untrusted Plan names.
- Subscription DTO projects elapsed usage periods into a future period without persisting mutations during GET.
- Usage reset is not represented as a subscription billing renewal.
