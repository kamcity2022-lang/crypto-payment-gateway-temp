using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Gateway.Examples.MerchantIntegration;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set environment variable {name} first.");

try
{
    // Explicit opt-in avoids accidentally creating an invoice with live credentials.
    if (Environment.GetEnvironmentVariable("GATEWAY_CONFIRM_CREATE") != "YES")
        throw new InvalidOperationException(
            "This sample creates a real invoice. Set GATEWAY_CONFIRM_CREATE=YES to opt in.");

    var baseUrl = Required("GATEWAY_BASE_URL").TrimEnd('/');
    var apiKey = Required("GATEWAY_API_KEY");
    var apiSecret = Required("GATEWAY_API_SECRET");
    var callbackUrl = Required("GATEWAY_CALLBACK_URL");
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("GATEWAY_BASE_URL must be an HTTPS URL.");

    const string requestPath = "/api/v1/invoices";
    const string rawQuery = "";
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
        System.Globalization.CultureInfo.InvariantCulture);
    var nonce = Guid.NewGuid().ToString("N");
    var payload = JsonSerializer.Serialize(new
    {
        fiatAmount = 10m, fiatCurrency = "USD",
        callbackUrl, expiryMinutes = 30
    });
    var body = Encoding.UTF8.GetBytes(payload);
    var signature = GatewaySignatures.SignApiRequest(
        "POST", requestPath, rawQuery, timestamp, nonce, body, apiSecret);

    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + requestPath)
    {
        Content = new ByteArrayContent(body)
    };
    request.Content.Headers.ContentType =
        new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    request.Headers.Add("X-API-Key", apiKey);
    request.Headers.Add("X-Signature", signature);
    request.Headers.Add("X-Timestamp", timestamp);
    request.Headers.Add("X-Nonce", nonce);
    // Reuse this key ONLY when retrying the same logical request and payload.
    request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

    using var response = await http.SendAsync(request);
    Console.WriteLine($"HTTP {(int)response.StatusCode}");
    Console.WriteLine(await response.Content.ReadAsStringAsync());
    if (!response.IsSuccessStatusCode) Environment.ExitCode = 1;
}
catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
