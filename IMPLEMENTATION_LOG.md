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