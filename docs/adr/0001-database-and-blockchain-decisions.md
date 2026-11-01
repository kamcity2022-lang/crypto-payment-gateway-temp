# ADR 0001: Core Architecture, Database, and Blockchain Decisions

## Status
Accepted

## Context
The system requires an atomic, non-custodial cryptocurrency payment gateway for USDT TRC-20 payments.

## Decisions
1. **Database Provider:** PostgreSQL with EF Core. Row-level locking (`SELECT ... FOR UPDATE`) is used on wallet allocations to guarantee zero derivation collisions under concurrent workloads.
2. **Blockchain Client & Finality:** TronGrid API with SolidityNode queries (`/walletsolidity/getnowblock` and `/walletsolidity/gettransactioninfobyid`). Solidified block heights are used to confirm incoming transfers.
3. **Financial Precision:** Integral scale using `long` for micro-units (Sun, 6 decimals for USDT). No floating-point types (`float`/`double`) in domain accounting.
4. **Outbox & Security:** Transactional Outbox pattern for webhooks with SSRF validation, constant-time HMAC comparison, and database-persisted replay nonces.