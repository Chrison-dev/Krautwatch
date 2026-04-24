using System.Text;
using Xunit.Abstractions;

/// <summary>
/// Delegating handler that prints every outbound request and inbound response
/// to the xUnit test output. Useful for documenting the HTTP protocol before
/// extracting calls to .bru / Postman / curl files.
/// </summary>
sealed class HttpTraceHandler(ITestOutputHelper output) : DelegatingHandler(new HttpClientHandler())
{
    // How many response-body characters to print in the summary line.
    // Set to 0 to suppress body preview; set to int.MaxValue to print everything.
    public int BodyPreviewLength { get; init; } = 800;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // ── Request ───────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"┌─ REQUEST ──────────────────────────────────────────────────────");
        sb.AppendLine($"│  {request.Method} {request.RequestUri}");
        foreach (var (k, v) in request.Headers)
            sb.AppendLine($"│  {k}: {string.Join(", ", v)}");
        sb.Append(    $"└────────────────────────────────────────────────────────────────");
        output.WriteLine(sb.ToString());

        // ── Response ──────────────────────────────────────────────────────────
        var resp = await base.SendAsync(request, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);

        sb.Clear();
        sb.AppendLine();
        sb.AppendLine($"┌─ RESPONSE ─────────────────────────────────────────────────────");
        sb.AppendLine($"│  {(int)resp.StatusCode} {resp.StatusCode}");
        sb.AppendLine($"│  Content-Type: {resp.Content.Headers.ContentType}");
        sb.AppendLine($"│  Body ({body.Length:N0} chars):");

        if (BodyPreviewLength > 0)
        {
            var preview = body.Length <= BodyPreviewLength
                ? body
                : body[..BodyPreviewLength] + $"… [{body.Length - BodyPreviewLength:N0} more chars]";
            sb.AppendLine($"│  {preview}");
        }

        sb.Append(    $"└────────────────────────────────────────────────────────────────");
        output.WriteLine(sb.ToString());

        // Replace content so callers can still read it.
        resp.Content = new StringContent(body, Encoding.UTF8,
            resp.Content.Headers.ContentType?.MediaType ?? "application/json");

        return resp;
    }
}
