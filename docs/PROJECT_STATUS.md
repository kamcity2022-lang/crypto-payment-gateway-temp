# Crypto Payment Gateway — Project Status

## Project Purpose and Architecture

A non-custodial cryptocurrency payment gateway for USDT TRC-20 payments on the TRON network. The system provides:
- Deterministic address derivation (BIP-44) with zero private key exposure
- Atomic invoice creation with idempotency protection
- Transactional Outbox pattern for reliable webhook delivery
- Multi-tenant merchant management with RBAC authorization
- HMAC-based API authentication for merchant integrations

## Current Technology Stack

- **Framework**: .NET 10, ASP.NET Core
- **Database**: PostgreSQL with EF Core
- **Authentication**: ASP.NET Core Identity (human users), HMAC (API clients)
- **Blockchain**: TRON (TRC-20 USDT) via TronGrid API
- **Security**: AES-GCM encryption, SSRF validation, constant-time HMAC comparison
- **Testing**: xUnit, FluentAssertions, Testcontainers

## Latest Verified Commit SHA

**Commit**: `72240b009e28e76c4baea2bde9be69032d8b5d6c` (Phase 2 checkpoint)

## Phase and Task Completion Status

### Phase 1: Core Payment Engine ✅ COMPLETED
- Invoice state machine with confirmation tracking
- TRON address derivation (BIP-44)
- Blockchain scanner with solidification verification
- Transactional Outbox webhook dispatcher
- HMAC authentication with replay protection
- Settlement and sweep functionality

### Phase 2: Commercial SaaS Product 🔄 IN PROGRESS

#### Task 1: Merchant Identity & Authentication ✅ COMPLETED
- ASP.NET Core Identity with ApplicationUser
- MerchantClaimToken with SHA256 hashing, single-use, time-limited
- Activation endpoint with FOR UPDATE SKIP LOCKED
- Login endpoint with RequireConfirmedEmail
- SuperAdmin bootstrap service
- CustomUserClaimsPrincipalFactory for MerchantId injection

#### Task 2: RBAC & Tenant-Aware Authorization ✅ COMPLETED
- 5 standard roles: SuperAdmin, Owner, Admin, Developer, Viewer
- MerchantIdHandler for tenant isolation
- Centralized policies: SuperAdminOnly, MerchantOwner, MerchantAdmin, MerchantDeveloper, MerchantViewer
- Separation of HMAC authentication from human-user authorization
- SuperAdmin cross-tenant access

#### Task 3: Merchant Dashboard MVP ✅ COMPLETED
- DashboardController with overview, profile, invoices endpoints
- Vanilla JS dashboard UI
- Invoice listing with pagination and filtering
- Profile update with domain methods
- SSRF validation for webhook URLs

#### Task 4: Super Admin Portal ✅ COMPLETED
- **4.1 Super Admin Management API**: Overview, merchant listing, merchant details
- **4.2 Merchant Lifecycle**: Activate/Deactivate endpoints with IsActive check
- **4.3 Super Admin UI**: Vanilla JS dashboard with login, overview, merchant management
- **4.4 Lifecycle Authorization**: MerchantIdHandler checks IsActive from database

#### Task 5: Plans, Subscriptions & Usage Enforcement ⏸️ PENDING
- Plan entity with limits
- Subscription lifecycle
- Server-side usage enforcement
- Usage ledger with concurrency control

#### Task 6: Audit, Support & Operational Readiness ⏸️ PENDING
- Audit log entity and service
- Support tools (read-only)
- Runbooks for incident response
- Backup and restore procedures
- Monitoring and alerting

#### Task 7: Merchant Documentation & Onboarding ⏸️ PENDING
- Quick start guide
- API documentation
- Webhook verification guide
- SDK examples

## Implemented Features

### Core Payment Engine
- ✅ Non-custodial TRON address derivation (BIP-44)
- ✅ Invoice creation with idempotency keys
- ✅ Payment transfer confirmation tracking
- ✅ Transactional Outbox webhook delivery
- ✅ HMAC authentication with replay protection
- ✅ Settlement address management
- ✅ Sweep task lifecycle

### Commercial SaaS
- ✅ Merchant registration with API key generation
- ✅ User invitation and activation flow
- ✅ Role-based access control (RBAC)
- ✅ Tenant isolation in dashboard
- ✅ Super Admin portal with merchant management
- ✅ Merchant lifecycle (activate/deactivate)
- ✅ Real-time IsActive check in authorization

## Known Issues and Deferred Work

### Critical Security Issues (Deferred to Production Hardening Phase)
- ⚠️ Hardcoded `MasterEncryptionKey` in `appsettings.json`
- ⚠️ Bootstrap credentials in configuration files
- ⚠️ Database credentials in `appsettings.json`
- ⚠️ `DevOptions` present in base configuration
- ⚠️ `DashboardController` directly accessing `DbContext` (violates Clean Architecture)

### Non-Blocking Review Observations
- Admin pagination tests could benefit from more edge cases
- Inline JavaScript event handlers in admin UI (acceptable for MVP)
- Webhook test-event uses merchant ID as aggregate ID (inconsistent with invoice events)
- Merchant registration returns 201 but Location header behavior needs verification

### Test Coverage Gaps
- Integration tests for concurrent merchant creation
- E2E tests for complete merchant onboarding flow
- Load testing for scanner performance
- Chaos testing for webhook delivery

## Test and CI Status

### Verified Results
- ✅ All Phase 1 tests passing (103+ tests)
- ✅ Phase 2 integration tests passing
- ✅ HMAC authentication tests passing
- ✅ Tenant isolation tests passing
- ✅ Super Admin authorization tests passing

### CI/CD Pipeline
- GitHub Actions workflow configured for .NET 10
- Automated build and test on push/PR to master
- No deployment automation yet

## Next Recommended Task

When development resumes, the recommended next task is:

**Task 5: Plans, Subscriptions & Usage Enforcement**

This task will implement:
1. Plan entity with merchant limits (invoice count, volume, etc.)
2. Subscription lifecycle management
3. Server-side usage enforcement at API level
4. Usage ledger with concurrency control
5. Admin override capabilities with audit trail

### Prerequisites for Task 5
- Complete security hardening (remove hardcoded secrets)
- Implement audit logging (Task 6 prerequisite)
- Consider implementing rate limiting before usage enforcement

## Architecture Decisions

See `docs/adr/0001-database-and-blockchain-decisions.md` for core architecture decisions.

## Development Guidelines

- Follow Clean Architecture principles
- Use conventional commits format
- All new features require integration tests
- No floating-point types in financial calculations
- SSRF validation for all external URLs
- Constant-time comparison for sensitive data

---

**Last Updated**: 2026-11-04  
**Status**: Phase 2 Checkpoint — Safe to Resume