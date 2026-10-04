// ProbeSite - tiny ASP.NET Core app to test, from MonsterASP.NET's servers, which shops answer normally.
// GET /probe?key=YOUR_KEY  -> JSON: this server's public IP + HTTP status for every store in targets.json.
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Change this before deploying so strangers cannot trigger requests from your site.
string Cfg(string name, string fallback = "") => builder.Configuration[name] is { Length: > 0 } v ? v : fallback;

// Settings come from appsettings.json (next to the .dll) or environment variables. See appsettings.json.
var probeKey = Cfg("PROBE_KEY", "change-me-before-deploy");

// /fetch only forwards requests to these hosts (so nobody can use your site as an open proxy).
var allowedHosts = Cfg("FETCH_ALLOWED_HOSTS", "www.amazon.ae,amazon.ae")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

var fetchHttp = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
{ Timeout = TimeSpan.FromSeconds(25) };
fetchHttp.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
fetchHttp.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

app.MapGet("/", () => "ProbeSite is running.");

app.MapGet("/probe", async (string? key, IWebHostEnvironment env) =>
{
    if (key != probeKey) return Results.Unauthorized();

    var cfgPath = Path.Combine(env.ContentRootPath, "targets.json");
    using var cfg = JsonDocument.Parse(await File.ReadAllTextAsync(cfgPath));
    var targets = cfg.RootElement.GetProperty("targets").EnumerateArray()
        .Select(t => (name: t.GetProperty("name").GetString()!, url: t.GetProperty("url").GetString()!))
        .ToList();
    targets.Add(("telegram-api (connectivity only)", "https://api.telegram.org/"));

    using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(20) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

    string ip;
    try { ip = (await http.GetStringAsync("https://api.ipify.org")).Trim(); }
    catch (Exception ex) { ip = "could not detect: " + ex.GetType().Name; }

    var results = new List<object>();
    foreach (var (name, url) in targets)
    {
        try
        {
            using var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            var wall = Regex.IsMatch(body, "api-services-support@amazon|Type the characters you see|_Incapsula_|px-captcha|cf-chl|Just a moment", RegexOptions.IgnoreCase);
            var hasPrice = Regex.IsMatch(body, @"(AED|Dhs?\.?|\bD)\s?\d[\d,]{2,}");
            var real = hasPrice && !wall;
            // For pages that are not real product pages, show the start of the text so we can see WHY (captcha, JS shell, redirect...).
            var snippet = real ? null : Regex.Replace(Regex.Replace(body, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]+>|\\s+", " ").Trim();
            if (snippet != null && snippet.Length > 160) snippet = snippet[..160];
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString();
            results.Add(new { name, status = (int)resp.StatusCode, bytes = body.Length, botWall = wall, looksLikeRealPage = real, finalUrl = finalUrl == url ? null : finalUrl, snippet });
        }
        catch (Exception ex)
        {
            results.Add(new { name, status = 0, error = ex.GetType().Name + ": " + ex.Message });
        }
        await Task.Delay(1000);
    }
    return Results.Json(new { serverPublicIp = ip, checkedAt = DateTime.UtcNow, results },
        new JsonSerializerOptions { WriteIndented = true });
});

// GET /fetch?key=KEY&url=ENCODED_URL -> returns the page body; the shop's real status is in the X-Upstream-Status header.
app.MapGet("/fetch", async (string? key, string? url, HttpContext ctx) =>
{
    if (key != probeKey) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)
        || (u.Scheme != "https" && u.Scheme != "http") || !allowedHosts.Contains(u.Host))
        return Results.BadRequest("url missing or host not allowed");
    try
    {
        using var resp = await fetchHttp.GetAsync(u);
        var body = await resp.Content.ReadAsStringAsync();
        ctx.Response.Headers["X-Upstream-Status"] = ((int)resp.StatusCode).ToString();
        return Results.Text(body, "text/html; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Problem("upstream failed: " + ex.GetType().Name, statusCode: 502);
    }
});

// ---------------- Telegram "/start" -> run the GitHub workflow ----------------
var tgToken   = Cfg("TELEGRAM_BOT_TOKEN");
var tgChat    = Cfg("TELEGRAM_CHAT_ID");
var tgSecret  = Cfg("TELEGRAM_WEBHOOK_SECRET");
var ghToken   = Cfg("GITHUB_TOKEN");
var ghRepo    = Cfg("GITHUB_REPO");                  // e.g. EyasWannous/Notify-me-for-nothing-phone
var ghFlow    = Cfg("GITHUB_WORKFLOW", "phone-watch.yml");
var ghRef     = Cfg("GITHUB_REF", "main");
var tgApi     = Cfg("TELEGRAM_API_BASE", "https://api.telegram.org");
var ghApi     = Cfg("GITHUB_API_BASE", "https://api.github.com");
var lastTrigger = DateTime.MinValue;
var triggerLock = new object();

var apiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
apiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneWatch-Trigger/1.0");

async Task TgSendAsync(string text)
{
    using var c = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = tgChat, ["text"] = text });
    await apiHttp.PostAsync($"{tgApi}/bot{tgToken}/sendMessage", c);
}

// One-time setup: open /telegram/setup?key=YOUR_PROBE_KEY in the browser to register the webhook.
app.MapGet("/telegram/setup", async (string? key, HttpContext ctx) =>
{
    if (key != probeKey) return Results.Unauthorized();
    if (string.IsNullOrEmpty(tgToken) || string.IsNullOrEmpty(tgSecret)) return Results.BadRequest("TELEGRAM_BOT_TOKEN / TELEGRAM_WEBHOOK_SECRET missing");
    var hookUrl = $"https://{ctx.Request.Host}/telegram/webhook";
    using var c = new FormUrlEncodedContent(new Dictionary<string, string>
    { ["url"] = hookUrl, ["secret_token"] = tgSecret, ["allowed_updates"] = "[\"message\"]" });
    var resp = await apiHttp.PostAsync($"{tgApi}/bot{tgToken}/setWebhook", c);
    return Results.Text($"webhook url: {hookUrl}\n{await resp.Content.ReadAsStringAsync()}");
});

app.MapPost("/telegram/webhook", async (HttpRequest req) =>
{
    // Only Telegram knows the secret header; only your own chat may trigger a run.
    if (string.IsNullOrEmpty(tgSecret) || req.Headers["X-Telegram-Bot-Api-Secret-Token"] != tgSecret)
        return Results.Unauthorized();

    string? text = null; string? chatId = null;
    try
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);
        var msg = doc.RootElement.GetProperty("message");
        text = msg.TryGetProperty("text", out var t) ? t.GetString() : null;
        chatId = msg.GetProperty("chat").GetProperty("id").GetRawText();
    }
    catch { return Results.Ok(); }

    if (chatId != tgChat) return Results.Ok();
    var cmd = (text ?? "").Split(' ', '@')[0].ToLowerInvariant();
    if (cmd is not ("/start" or "/check")) return Results.Ok();

    lock (triggerLock)
    {
        if ((DateTime.UtcNow - lastTrigger).TotalSeconds < 60)
        {
            _ = TgSendAsync("A check was started less than a minute ago - please wait for its result.");
            return Results.Ok();
        }
        lastTrigger = DateTime.UtcNow;
    }

    try
    {
        using var r = new HttpRequestMessage(HttpMethod.Post, $"{ghApi}/repos/{ghRepo}/actions/workflows/{ghFlow}/dispatches")
        { Content = new StringContent($"{{\"ref\":\"{ghRef}\"}}", System.Text.Encoding.UTF8, "application/json") };
        r.Headers.Authorization = new("Bearer", ghToken);
        r.Headers.Accept.ParseAdd("application/vnd.github+json");
        r.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var resp = await apiHttp.SendAsync(r);
        await TgSendAsync(resp.IsSuccessStatusCode
            ? "Check started - the result arrives in about a minute."
            : $"Could not start the check (GitHub answered {(int)resp.StatusCode}). Check the GitHub token.");
    }
    catch (Exception ex) { await TgSendAsync("Could not start the check: " + ex.GetType().Name); }
    return Results.Ok();
});

app.Run();