# Crypto Payment Gateway

Non-custodial .NET 10 / PostgreSQL payment gateway prototype for TRON USDT TRC-20. Includes HMAC merchant APIs, invoice checkout, blockchain scanning, webhook outbox, and Admin/Merchant dashboards.

**NOT PRODUCTION READY.** This repository does not include a stand-alone testnet mode in its Worker. Never send real funds without completing security and operational launch gates.

## Guides

- Merchant onboarding and HMAC/webhook integration: [docs/MERCHANT_ONBOARDING.md](docs/MERCHANT_ONBOARDING.md)
- Local setup, runtime configuration and launch gates: [docs/OPERATIONAL_READINESS.md](docs/OPERATIONAL_READINESS.md)
- Executable .NET 10 integration example: [examples/merchant-integration](examples/merchant-integration)
- Engineering progress: [IMPLEMENTATION_LOG.md](IMPLEMENTATION_LOG.md)
- Historical Nov 4 checkpoint (not latest): [docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md)

## Build and tests

    dotnet restore CryptoPaymentGateway.slnx
    dotnet build CryptoPaymentGateway.slnx --no-restore
    dotnet test CryptoPaymentGateway.slnx --no-build

Integration tests require Docker and Testcontainers. The API and Worker each require correct PostgreSQL and secure external config. The merchant sample refuses live invoice creation unless explicitly enabled.
