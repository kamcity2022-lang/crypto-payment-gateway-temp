# Merchant onboarding — USDT TRC-20 / TRON

This describes the CURRENT implemented integration contract, not a production launch. Only TRON USDT payments with USD fiat invoicing are currently supported. There is no standalone blockchain sandbox switch.

## 1. Merchant setup

1. SuperAdmin provisions a merchant with a valid TRON account xpub and HTTPS callback. An xpub is public derivation data: NEVER share private keys or seed phrases.
2. Store the initial API key, API secret and webhook secret from provisioning securely. New credentials can be created or rotated by Merchant Owner/Admin in the Dashboard. Secrets appear once only.
3. SuperAdmin issues an Owner activation token. POST to /api/v1/auth/activate with JSON properties email, password, token. Activate then verify the email before login. The current repository has no full production email verification sender, so onboarding remains an operational launch blocker outside Development.
4. POST /api/v1/auth/login with email/password; use /dashboard.html. Cookie authentication is distinct from the merchant HMAC integration.
5. Merchant Owner may invite Admin, Developer and Viewer team members using email-bound single-use tokens; sending the invitation securely is manual.

## 2. Create invoice over HMAC API

Endpoint: POST /api/v1/invoices. Required headers: X-API-Key, X-Timestamp, X-Nonce, X-Signature. Recommended Idempotency-Key: reuse the same value with identical payload when retrying the same logical operation (successful responses cached up to 24h).

Body example (single-line, UTF-8):

    {"fiatAmount":10,"fiatCurrency":"USD","callbackUrl":"https://merchant.example/webhook","expiryMinutes":30}

USD is the only supported fiat. A fresh exchange-rate quote, active subscription, sufficient invoice and USD quota, and a validated HTTPS callback URL are required. Supported invoice amount range: 0.01–1,000,000 USD.

### Canonical HMAC input (exact bytes)

Serialize JSON ONCE and SHA256 the exact raw UTF-8 body bytes. Concatenate SIX lines with LF (0x0A), without a trailing LF:

    UPPERCASE_HTTP_METHOD
    REQUEST_PATH
    RAW_QUERY_STRING
    UNIX_TIMESTAMP_SECONDS
    NONCE
    LOWERCASE_SHA256_HEX_OF_EXACT_BODY_BYTES

Path and raw query are independent lines. For POST /api/v1/invoices with no query, the third line is empty. For GET with query, include the leading question mark in the RAW_QUERY_STRING. Method is uppercase. A GET with no body uses the SHA256 digest of zero bytes. Timestamp is Unix seconds (±300-second tolerance); nonce is unique within a merchant (max 128 characters). Compute lowercase hex HMAC-SHA256 using the UTF-8 API secret and canonical UTF-8 bytes. Changing JSON whitespace or order AFTER signing invalidates the signature. Signed body size limit is 1 MiB.

Static signing vector (historical timestamp; not for use as a live request):

    Method: POST
    Path: /api/v1/invoices
    Query: [empty]
    Timestamp: 1730880000
    Nonce: nonce-1
    API secret: example-secret-not-real
    Body: {"fiatAmount":10,"fiatCurrency":"USD","callbackUrl":"https://merchant.example/webhook","expiryMinutes":30}
    Body hash: 82121c8993a07f87ce33148230e361a86de08af6a7a7c99b726fb0c41acb668f
    Signature: 15d4c9f9f7416afd907eb7ae842a748bff32c619512a06eca2996b400a56823c

The executable .NET 10 sample is in examples/merchant-integration/. It refuses to send a request unless GATEWAY_CONFIRM_CREATE=YES is set intentionally. Configure GATEWAY_BASE_URL (HTTPS), GATEWAY_API_KEY, GATEWAY_API_SECRET and GATEWAY_CALLBACK_URL (HTTPS), then run:

    dotnet run --project examples/merchant-integration/Gateway.MerchantIntegration.Example.csproj

A 201 response contains invoiceId, depositAddress, expectedCryptoAmount, expectedUnits (micro-USDT), exchangeRate, checkoutUrl, status and timestamps. Creating an invoice does NOT mean receiving a payment. Duplicate idempotency-key requests with identical body return the original response; a different body produces 409.

## 3. Customer checkout and finality

The returned checkoutUrl displays payment instructions; GET /api/v1/checkout/{token} reports the derived current status. Treat checkout URLs as sensitive bearer-style links. The Worker verifies transfer event, USDT contract address, recipient, amount, success receipt and solidification. States include Pending, PartiallyPaid, Paid, Overpaid, Underpaid, Expired and Cancelled. Use the signed webhook and/or authenticated merchant GET /api/v1/invoices/{invoiceId} to settle orders. Do not trust browser polling alone. Late and extra transfers need separate merchant business logic.

## 4. Verify incoming webhooks

Worker sends HTTP POST JSON with headers:

- X-CryptoGateway-Event-Id: stable outbox event UUID (must be durably deduplicated)
- X-CryptoGateway-Timestamp: Unix seconds
- X-CryptoGateway-Signature: lowercase hex HMAC-SHA256(webhookSecret, timestamp + "." + EXACT raw HTTP body bytes)

Webhook secret is different from the API secret. Verify on raw body bytes before JSON parsing, reject stale timestamps, use constant-time comparison, and enforce durable unique processing for Event-Id. Return a 2xx only AFTER durably accepting the event. The exact same event may be redelivered after previous success; state changes for the same invoice may also generate distinct events. Treat delivery as AT-LEAST-ONCE.

Payload example:

    {"event":"invoice.paid","invoiceId":"00000000-0000-0000-0000-000000000001","status":"Paid","paidUnits":10000000,"expectedUnits":10000000,"txHash":"example"}

Possible events: invoice.partiallypaid, invoice.paid, invoice.overpaid, invoice.underpaid, invoice.expired, webhook.test; specifics depend on transitions. Failures retry with quadratic backoff (10,40,90,160 seconds, up to 5 failed attempts). Manual redelivery and history are in the Merchant Webhooks dashboard.

Static webhook verification vector:

    Timestamp: 1730880000
    Secret: example-webhook-secret
    Body: {"event":"invoice.paid","invoiceId":"00000000-0000-0000-0000-000000000001"}
    Signature: e16973ce93b586b96ca3e65e46f563daa2a0dc8add3c191c1abf8b02117517c1

The executable GatewaySignatures.VerifyWebhook function in the example uses raw bytes, timestamp checks and constant-time comparison.

## 5. HTTP troubleshooting

- 401: bad or revoked key, bad signature or expired timestamp. Resign the exact body with current timestamp/new nonce.
- 409: replayed nonce or idempotency conflict. Investigate before retrying.
- 413: signed request body exceeds 1 MiB.
- 403 SUBSCRIPTION_NOT_ACTIVE: no active subscription.
- 403 SUBSCRIPTION_INVOICE_LIMIT_REACHED / SUBSCRIPTION_VOLUME_LIMIT_REACHED: quota exhausted.
- 503 EXCHANGE_RATE_UNAVAILABLE: fresh market rate missing or invalid; retry safely with stable idempotency key.
- 400: unsupported fiat, SSRF-unsafe callback or invalid input.

## Integration acceptance checklist

- [ ] Owner login and confirmed email work in the deployment.
- [ ] API credentials are stored securely; body and query tampering are rejected.
- [ ] Invoice returns 201 and a separate derived deposit address.
- [ ] Idempotency replay does not consume quota twice.
- [ ] On-chain USDT transfer is solidified before order fulfillment.
- [ ] Webhook raw body signature and persistent Event-Id deduplication work.
- [ ] Retries, late, underpayment and overpayment are handled.
- [ ] No real funds used until the operational and security gates have passed.

See docs/OPERATIONAL_READINESS.md before deployment.
