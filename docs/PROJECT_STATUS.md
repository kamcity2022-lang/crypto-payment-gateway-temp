# Crypto Payment Gateway — Project Status

## 1. Project Purpose and Architecture
A non-custodial cryptocurrency payment gateway for USDT TRC-20 payments on the TRON network. The system provides:
- Deterministic address derivation (BIP-44) with zero private key exposure.
- Atomic invoice creation with idempotency protection.
- Transactional Outbox pattern for reliable webhook delivery.
- Multi-tenant merchant management with RBAC authorization.
- HMAC-based API authentication for merchant integrations.

## 2. Current Technology Stack
- **Framework:** .NET 10, ASP.NET Core
- **Database:** PostgreSQL with EF Core
- **Authentication:** ASP.NET Core Identity (human users), HMAC (API clients)
- **Blockchain:** TRON (TRC-20 USDT) via TronGrid API
- **Security:** AES-GCM encryption, SSRF validation, constant-time HMAC comparison
- **Testing:** xUnit, FluentAssertions, Testcontainers (PostgreSQL)

## 3. Latest Verified Commit SHA
**Commit:** `72240b009e28e76c4baea2bde9be69032d8b5d6c` (Phase 2 Checkpoint - Security Fix Applied)  
**Date:** 2026-11-04

## 4. Phase and Task Completion Status

### Phase 1: Core Payment Engine ✅ COMPLETED
- Invoice state machine with confirmation tracking.
- TRON address derivation (BIP-44).
- Blockchain scanner with solidification verification.
- Transactional Outbox webhook dispatcher.
- HMAC authentication with replay protection.
- Settlement and sweep functionality.

### Phase 2: Commercial SaaS Product ✅ COMPLETED (Checkpoint Reached)
- **Task 1: Merchant Identity & Authentication** ✅ COMPLETED  
  (ASP.NET Core Identity, Claim Tokens, Activation, SuperAdmin bootstrap)
- **Task 2: RBAC & Tenant-Aware Authorization** ✅ COMPLETED  
  (5 standard roles, `MerchantIdHandler` with mandatory DB `IsActive` check, SuperAdmin cross-tenant access)
- **Task 3: Merchant Dashboard MVP** ✅ COMPLETED  
  (Overview, profile, invoices, SSRF validation, tenant isolation)
- **Task 4: Super Admin Portal** ✅ COMPLETED  
  (4.1 Management API, 4.2 Lifecycle Activate/Deactivate, 4.3 Vanilla JS UI, 4.4 Integration & Security Review)
- **Task 5: Plans, Subscriptions & Usage Enforcement** ⏸️ PENDING
- **Task 6: Audit, Support & Operational Readiness** ⏸️ PENDING
- **Task 7: Merchant Documentation & Onboarding** ⏸️ PENDING

## 5. Implemented Features
- Non-custodial TRON address derivation (BIP-44).
- Invoice creation with idempotency keys.
- Payment transfer confirmation tracking.
- Transactional Outbox webhook delivery.
- HMAC authentication with replay protection.
- Super Admin portal with merchant lifecycle management (Activate/Deactivate).
- **Real-time IsActive enforcement:** Merchant users are immediately denied access upon deactivation via centralized `MerchantIdHandler` database checks.

## 6. Known Issues and Deferred Work (Production Hardening Phase)
⚠️ *The following items are explicitly deferred to the final Security & Production Hardening phase and must be addressed before going live:*
- **Hardcoded MasterEncryptionKey:** Currently present in `appsettings.json`. Must be moved to User Secrets (Dev) or Environment Variables / Key Vault (Prod).
- **Bootstrap Credentials:** `SuperAdminEmail` and `SuperAdminPassword` are in configuration files.
- **Database Credentials:** Connection string is in base configuration.
- **DevOptions:** `BypassEmailConfirmation` is present in base configuration (must be strictly environment-gated).
- **Clean Architecture Violation:** `DashboardController` directly accesses `GatewayDbContext`. This was deferred to avoid broad refactoring during Phase 2 checkpoint.
- **Non-blocking Observations:**
    - Admin pagination/filter tests could benefit from more edge cases.
    - Inline JavaScript event handlers in admin UI (acceptable for MVP, but CSP should be implemented later).
    - Webhook test-event uses merchant ID as aggregate ID (slightly inconsistent with invoice events).
    - Merchant registration `Location` header behavior needs final verification.

## 7. Test and CI Status
- **Unit & Integration Tests:** ✅ **VERIFIED**  
  All tests pass locally, including the newly enforced database-backed `IsActive` checks in `AuthorizationTests` and `DashboardTests`. No tests bypass the database check.
- **CI/CD Pipeline:** ⚠️ **UNVERIFIED**  
  GitHub Actions workflow (`.github/workflows/dotnet-ci.yml`) exists but has not been triggered/verified for this specific checkpoint commit. Local execution is the source of truth for this report.

## 8. Exact Next Recommended Task
When development resumes, the recommended next task is:  
**Task 5: Plans, Subscriptions & Usage Enforcement**

### Prerequisites for Task 5:
1. Complete security hardening (remove all hardcoded secrets from `appsettings.json`).
2. Implement basic audit logging (Task 6 prerequisite) to track plan overrides and usage limit breaches.
3. Consider implementing rate limiting before enforcing usage limits at the API level.

---
**Last Updated:** 2026- if development resumes.