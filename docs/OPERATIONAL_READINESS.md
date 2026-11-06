# Operational readiness (NOT PRODUCTION APPROVED)

This checklist documents a prototype; it does not certify a live/testnet blockchain acceptance test, external security assessment, verified backup restoration or production deployment.

## Architecture and local run

- API: .NET 10 ASP.NET Core, PostgreSQL, EF Core migrations on startup, SuperAdmin bootstrap. Static /dashboard.html, /admin.html and /checkout.html. GET /healthz checks DATABASE CONNECTIVITY ONLY.
- Worker: .NET 10 BackgroundService polling every ~5 seconds, with TRON scanner and webhook sender. Uses the SAME PostgreSQL DB and SAME AES master key as the API.
- Blockchain: Worker and sweep services now select fixed TronGrid hosts via Tron:Network (Mainnet, Nile or Shasta). Missing/unknown network fails closed; Mainnet requires Tron:EnableMainnet=true. The token contract must be explicitly configured, and testnets reject the canonical Mainnet USDT contract. Automated integration tests mock TronGrid.
- Exchange rates: Coinbase USDT/USD quote provider; unavailable/invalid quote blocks invoice creation (503). No silent 1:1 fallback.

Install .NET 10 SDK, PostgreSQL and Docker (Testcontainers tests). Configure secrets OUTSIDE source code. Avoid copying tracked appsettings credentials into deployment.

Required configuration keys (ASP.NET Core environment variable equivalents use double underscore):

| Key | Target | Meaning |
| --- | --- | --- |
| ConnectionStrings__DefaultConnection | API + Worker | Same PostgreSQL database |
| Security__MasterEncryptionKey | API + Worker | Same 64-character hex / 32-byte AES key |
| Bootstrap__SuperAdminEmail | API bootstrap | New local SuperAdmin only |
| Bootstrap__SuperAdminPassword | API bootstrap | New local SuperAdmin only |
| Tron__Network | API sweep operations + Worker | Required: Mainnet, Nile, or Shasta. No implicit default. |
| Tron__EnableMainnet | API sweep operations + Worker | Explicit true required for Mainnet |
| Tron__UsdtContractAddress | API sweep operations + Worker | Reviewed TRON USDT contract on selected network; testnet must use its own validated deployed token |
| Tron__Scanner__LatePaymentRetentionDays | Worker optional | Defaults 30 |
| Tron__Scanner__OverpaymentRetentionDays | Worker optional | Defaults 7 |
| DevOptions__BypassEmailConfirmation | API only, Development | Must be false for production |

DO NOT rotate the AES key without first planning to decrypt/re-encrypt existing merchant/outbox secrets or preserving the old key safely. Arbitrarily replacing it breaks existing encrypted data.

Local commands:

    dotnet restore CryptoPaymentGateway.slnx
    dotnet build CryptoPaymentGateway.slnx --no-restore
    dotnet test CryptoPaymentGateway.slnx --no-build
    dotnet run --project src/Presentation/Gateway.Api/Gateway.Api.csproj --launch-profile https

Before starting the Worker, set Tron__Network to Nile or Shasta for tests; use a **separate database, wallet/xpub, merchant data, API keys and webhook receiver** for each network. TRON address prefixes overlap across networks and the current database schema does not tag invoices by chain network. This PR isolates HTTP routing but DOES NOT make shared mainnet/testnet database data safe. You must verify that the chosen testnet TRC-20 contract is deployed, 6-decimal-compatible and matches the intended test asset. A plausible-looking address alone does not prove it is a valid token contract.

Mainnet requires BOTH Tron__Network=Mainnet and Tron__EnableMainnet=true (and the canonical USDT contract). Never enable it casually.

If intentionally enabled after configuration review:

    dotnet run --project src/Presentation/Gateway.Worker/Gateway.Worker.csproj

Start API/DB alone to smoke-check /healthz, migrations and authenticated admin/merchant flows without blockchain traffic. Docker must be running for Testcontainers.

## Mandatory pre-production release gates

- [ ] Remove hardcoded config credentials, use central secrets manager, and establish controlled encryption key rotation.
- [ ] Provide working production email verification and secure invite delivery.
- [ ] Implement CSRF protection for cookie-authenticated state-changing dashboard/admin endpoints and rate limiting.
- [ ] Review TLS, Cookie, CORS/CSP, caching, DNS rebinding and outbound SSRF behavior in the actual deployment network.
- [ ] Verify real/testnet receipts, deployed contract decimals and known wallet derivation vectors under an isolated provider before moving real funds. Enforce separate DB/deployment per chain; current schema has no immutable chain-environment identifier.
- [ ] Implement independent Worker liveness and metrics: scanner lag, TronGrid errors/rate limiting, quote age, webhook backlog/failed retries.
- [ ] Create PostgreSQL encrypted backups and perform a full RESTORE DRILL; store the necessary decryption key separately; define RPO/RTO.
- [ ] Implement audit retention, tamper-resistant offsite export and coverage for authentication, settlement and operational overrides. Audit PR #15 is separate and not on master at the time of writing.
- [ ] Review Sweep/Settlement custody and authorization assumptions with a qualified reviewer.
- [ ] Run restart, concurrent Worker, load, lease expiry, outage and migration/rollback exercises.
- [ ] Complete operator support, incident and billing policies. Subscription price fields and quotas exist; automatic subscription billing is NOT implemented.
- [ ] Pilot only after security review, with strictly controlled value.

## Troubleshooting runbook

| Symptom | Where to inspect | Immediate response |
| --- | --- | --- |
| /healthz unhealthy | PostgreSQL and API connection config | Restore DB; do not assume Worker health |
| Invoice 503 | Coinbase USDT/USD API and quote freshness/depeg validation | Pause/retry, no artificial 1:1 price |
| Pending invoice | Worker logs, TronGrid solidity, invoice scan cursor | Investigate; NEVER mark paid without chain evidence |
| Webhook retry | Merchant Webhooks history, OutboxEvents and endpoint TLS | Correct receiver, then manually redeliver with Event-Id dedup |
| HMAC 401 | Timestamp skew, raw method/path/query/body, API key status | Resign current bytes, rotate leaked keys |
| Master key incident | Encrypted merchant/webhook and outbox data | Preserve decryptability; plan controlled re-encryption before swap |

## Honest project status

docs/PROJECT_STATUS.md is a dated historical checkpoint (Nov 4, 2026); it should not be read as an up-to-date acceptance report. IMPLEMENTATION_LOG.md and merged GitHub PRs are the chronological evidence. Phase 3 multi-network/multi-token features are not prerequisites for a controlled existing TRON/USDT MVP pilot. Green CI is NOT sufficient to declare payments safe for production.
