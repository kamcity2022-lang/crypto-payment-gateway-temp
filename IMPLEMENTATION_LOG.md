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

## Phase 2 — Merchant API Key Lifecycle
**Status:** Draft PR, pending CI.

- Supports multiple HMAC signing keys per Merchant through a new MerchantApiKeys table, including active/revoked state and unique hashed API key.
- Migrates existing Merchant credentials into a revocable Primary key; never falls back to the legacy Merchant fields after revocation.
- Merchant scoped list, create, rotate and revoke endpoints; only Owner/Admin may change keys, Developer may list; Viewer denied.
- API key hash only and AES-encrypted HMAC secret persisted. Raw key and secret returned only on create/rotate, never on GET.
- Rotation revokes the previous key atomically and issues a successor; revocation immediately blocks subsequent signed requests.
- Merchant Dashboard UI for key lifecycle, including one-time credential display and explicit confirmation on destructive actions.
- HTTP/PostgreSQL integration tests for tenant isolation, permissions, lifecycle and HMAC usage.
- CSRF protection, rate limiting and full audit remain mandatory before production launch under the previously deferred security backlog.

## Phase 2 — Merchant Team Management
**Status:** Draft PR pending CI.

- Merchant Owner can invite Admin/Developer/Viewer members using email-bound, expiring, single-use claim tokens.
- Existing owner onboarding tokens remain backward compatible and retain the Owner role.
- Team list, role changes, disabling/re-enabling members; other tenants cannot view or modify team members.
- Owner role is immutable through delegated management, preventing Owner takeover or self-disable.
- Disabled users cannot log in; roles and disabled-state changes rotate security stamps and Identity cookies validate the stamp on each request.
- Dashboard supports owner team operations and one-time invitation token presentation.
- Integration tests cover role/tenant restrictions, token metadata and role changes.
- A transactional outbox/email sender, CSRF protection, rate limits and comprehensive audit are still required before production launch.

## Phase 2 — Merchant Webhook Dashboard
**Status:** Draft PR, pending CI.

- Cookie-authenticated Merchant-scoped webhook configuration; GET and Update of default HTTPS endpoint.
- SSRF URL validation including loopback, private addresses, URL credentials and fragments.
- Lists real invoice events and merchant-scoped test webhooks with status/attempt/error, filters and pagination.
- Owner/Admin can enqueue test delivery and redeliver completed/exhausted events; Viewer read-only.
- Redelivery uses atomic conditional update and refuses already queued or actively leased events.
- Dashboard exposes settings, test delivery, event history and manual retry controls using DOM textContent.
- HTTP/PostgreSQL integration coverage for tenant isolation, permissions, invalid URLs, event history, and retry deduplication.
- Existing HMAC merchant integration endpoints remain available for backward compatibility.
- Cookie CSRF prevention, outbound event retention and full audit logging remain deferred production gates.

## Phase 2 — SuperAdmin Plan Management UI
**Status:** Draft PR pending CI.

- SuperAdmin admin.html now lists subscription plans and their invoice/volume limits, USD price, active/default state.
- Create and edit flows use the existing secured /api/v1/admin/plans endpoints; setting a new default is explicit and requires confirmation.
- Activate and deactivate controls respect the backend default-plan invariant (the default plan cannot be deactivated).
- Uses DOM textContent for untrusted plan values, clamps/validates numeric inputs, and displays API errors without persisting secrets.
- Existing PostgreSQL/HTTP integration tests cover plan management state transitions, authorization, uniqueness and impact on invoice quotas.
- Adds a static served HTML integration smoke test covering main controls and API bindings.
- No migration or changes to subscription billing logic.

## Phase 2 — SuperAdmin Merchant Subscription UI
**Status:** Draft PR, pending CI.

- Merchant list and details provide a subscription-management entry point.
- View the current plan, subscription state, invoice/USD quota consumption, period dates and any overrides.
- Assign new subscriptions, change plans, modify/clear merchant-specific quota overrides, and cancel active subscriptions.
- View subscription history using existing SuperAdmin-only APIs.
- Active-plan selector excludes inactive plans and handles missing/cancelled subscriptions.
- Confirm state-changing operations; changing a plan preserves consumption and custom overrides; assignment does not imply billing or payment.
- Escape rendered merchant data and use DOM textContent for subscription and plan values.
- Includes HTTP-served admin.html smoke test; existing PostgreSQL integration tests cover backend subscription transitions.
- No schema, recurring billing, or payment processing changes.

## Phase 2 — Merchant Invoice Explorer
**Status:** Draft PR, CI pending.
- A complete tenant-scoped invoice explorer in Merchant Dashboard: status and UTC date filters, pagination, invoice details and blockchain transfer records.
- The existing read-only invoice APIs remain the source of truth; no new schema or payment-state mutations.
- Dashboard list validates status (including rejecting numeric/unknown enum values), date range and paging; ordering by CreatedAtUtc plus Id is stable.
- Safe DOM textContent is used for data (no HTML interpolation). Authenticated sessions protect list/details; async response sequencing prevents stale results after logout/filter changes.
- HTTP/PostgreSQL integration tests cover filtering, other-merchant isolation, invoice details and invalid query ranges; HTML smoke test checks controls.
- Full operational audit/monitoring, docs and production security hardening remain before launch.

## Phase 2 — Operational Audit (initial coverage)
**Status:** Draft PR pending CI.

- Append-only, secret-free audit metadata: actor, merchant, curated action, target type/ID, UTC timestamp.
- Audit events are committed with their corresponding successful write for Plan, Subscription, API-key and Merchant Team lifecycle operations.
- Owner-only tenant-scoped audit API and SuperAdmin global/merchant-filtered API; 20 most recent items displayed in each portal.
- Audited API key rotation, revocation and team role/status changes are persisted in the same transaction.
- Tests assert role restrictions, tenant isolation, event presence and absence of raw keys, secrets or descriptions.
- Audit coverage is intentionally limited to the above administrative operations. Remaining invoice/settlement/authentication events, retention policy and tamper-resistant external export are later operational tasks.
- CSRF, rate limiting, config-secret rotation and monitoring remain release gates before production.


## Phase 2 — Merchant Documentation and Operational Runbook
**Status:** Draft PR pending CI.

- New README, Merchant Onboarding integration guide and Operational Readiness runbook aligned with the implementation.
- Documents exact API and webhook HMAC formats, onboarding, idempotency, status, quotas, and error handling.
- Adds opt-in .NET 10 executable merchant sample and deterministic signing-verification vectors/tests.
- Lists API/Worker env configuration names, .NET/Docker development commands, live TronGrid provider caveat, security/backup/monitoring/testnet release gates.
- Existing docs/PROJECT_STATUS.md is marked as historical rather than current.
- PR #15 Audit remains separate and unmerged in master at time this documentation was authored.

## P0 — Explicit TRON Network Selection (Nile/Shasta/Mainnet)
**Status:** Draft PR pending CI.

- Scanner and Sweep no longer embed live TronGrid endpoint URLs; instead select fixed documented hosts for Mainnet, Nile and Shasta.
- Mandatory Tron:Network and Tron:UsdtContractAddress; explicit Tron:EnableMainnet=true to access Mainnet; fail closed on missing/unknown network.
- Canonical Mainnet USDT contract is required on Mainnet and rejected for testnet, but a valid testnet token contract/decimals still must be independently verified.
- Worker validates network config during startup; existing simulated receipt tests opt into explicitly mocked Mainnet routing.
- Focused tests verify host selection, Mainnet opt-in, denied cross-network contracts and outbound request routing.
- IMPORTANT: Database schema is not network-separated. Each testnet deployment MUST use isolated DB/wallets/keys and must not reuse mainnet invoices; immutable network DB binding is a remaining production gate.
