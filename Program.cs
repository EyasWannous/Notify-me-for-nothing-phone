// PhoneWatch - daily stock/price checker for the Nothing Phone (4a) in the UAE.
// Usage:  dotnet run -- [folderWithTargets.json]
// Writes state.json (last result per store) and history.csv (one row per check) in that folder.
// Optional Telegram alerts: set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID environment variables.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var dir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var cfgPath = Path.Combine(dir, "targets.json");
var statePath = Path.Combine(dir, "state.json");
var historyPath = Path.Combine(dir, "history.csv");

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };

if (!File.Exists(cfgPath))
{
    Console.Error.WriteLine($"targets.json not found in {dir}");
    return 1;
}

var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath), jsonOpts)!;
var state = File.Exists(statePath)
    ? JsonSerializer.Deserialize<Dictionary<string, Result>>(File.ReadAllText(statePath), jsonOpts) ?? new()
    : new Dictionary<string, Result>();

using var http = new HttpClient(new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = true
}) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

var now = DubaiNow();
Console.WriteLine($"PhoneWatch  {now:yyyy-MM-dd HH:mm}");
if (DateTime.TryParse(cfg.BlackFriday, out var bf))
{
    var days = (bf.Date - now.Date).Days;
    Console.WriteLine(days >= 0 ? $"Days to Black Friday ({bf:dd MMM}): {days}" : "Black Friday has passed.");
}
Console.WriteLine();

var alerts = new List<string>();
var rows = new List<(Target t, Result r)>();
var rng = new Random();

foreach (var t in cfg.Targets)
{
    var res = await CheckAsync(t);
    rows.Add((t, res));

    state.TryGetValue(t.Url, out var prev);
    var msg = Evaluate(t, prev, res);
    if (msg != null) alerts.Add(msg);

    state[t.Url] = res;
    AppendHistory(t, res);

    await Task.Delay(rng.Next(1500, 3500)); // be polite to the shops
}

File.WriteAllText(statePath, JsonSerializer.Serialize(state, jsonOpts));

// ---- summary ----
foreach (var plan in new[] { "A", "B" })
{
    var label = plan == "A" ? "PLAN A  (8GB + 256GB)" : "PLAN B  (8GB + 128GB)";
    Console.WriteLine(label);
    foreach (var (t, r) in rows.Where(x => x.t.Plan == plan).OrderBy(x => x.r.Status == "InStock" ? 0 : 1).ThenBy(x => x.r.Price ?? decimal.MaxValue))
    {
        var price = r.Price.HasValue ? $"AED {r.Price:N0}" : "-";
        Console.WriteLine($"  {t.Name,-30} {r.Status,-11} {price,-12} {r.Detail}");
    }
    var best = rows.Where(x => x.t.Plan == plan && x.r.Status == "InStock" && x.r.Price.HasValue)
                   .OrderBy(x => x.r.Price).FirstOrDefault();
    Console.WriteLine(best.t != null
        ? $"  -> best in stock: {best.t.Name} at AED {best.r.Price:N0}"
        : "  -> nothing confirmed in stock");
    Console.WriteLine();
}

if (rows.Any(x => x.r.Status is "Blocked" or "Error"))
    Console.WriteLine("Note: 'Blocked'/'Error' rows could not be read automatically - open those links by hand.\n");

var sendSummary = string.Equals(Environment.GetEnvironmentVariable("SEND_SUMMARY"), "true", StringComparison.OrdinalIgnoreCase);
var parts = new List<string>();

if (alerts.Count > 0)
    parts.Add("ALERTS:\n" + string.Join("\n", alerts));
else
    Console.WriteLine("No changes worth an alert.");

if (sendSummary)
    parts.Add(BuildSummary(rows, now));

if (parts.Count > 0)
{
    var text = "Nothing Phone (4a)\n\n" + string.Join("\n\n", parts);
    Console.WriteLine("=== TELEGRAM MESSAGE ===\n" + text);
    await SendTelegramAsync(text);
}
return 0;

// ======================= functions =======================

static string BuildSummary(List<(Target t, Result r)> rows, DateTime now)
{
    var sb = new StringBuilder($"Daily summary {now:dd MMM HH:mm}\n");
    foreach (var plan in new[] { "A", "B" })
    {
        sb.Append(plan == "A" ? "Plan A (8/256): " : "Plan B (8/128): ");
        var best = rows.Where(x => x.t.Plan == plan && x.r.Status == "InStock" && x.r.Price.HasValue)
                       .OrderBy(x => x.r.Price).FirstOrDefault();
        sb.AppendLine(best.t != null ? $"best in stock {best.t.Name} at AED {best.r.Price:N0}" : "nothing confirmed in stock");
    }
    var unreadable = rows.Count(x => x.r.Status is "Blocked" or "Error" or "Unknown");
    sb.Append($"Could not read {unreadable} of {rows.Count} stores - check those by hand.");
    return sb.ToString();
}

static DateTime DubaiNow()
{
    foreach (var id in new[] { "Asia/Dubai", "Arabian Standard Time" })
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id)); }
        catch { /* try next id */ }
    }
    return DateTime.UtcNow.AddHours(4); // UAE is UTC+4 all year
}

string? Evaluate(Target t, Result? prev, Result cur)
{
    if (cur.Status != "InStock") return null;

    var wasInStock = prev?.Status == "InStock";
    var threshold = cfg.AlertBelow.TryGetValue(t.Plan, out var th) ? th : (decimal?)null;

    if (!wasInStock)
        return $"[Plan {t.Plan}] BACK IN STOCK: {t.Name}" + (cur.Price.HasValue ? $" at AED {cur.Price:N0}" : "") + $"\n{t.Url}";

    if (cur.Price.HasValue && prev!.Price.HasValue && cur.Price < prev.Price)
        return $"[Plan {t.Plan}] PRICE DROP: {t.Name} AED {prev.Price:N0} -> {cur.Price:N0}\n{t.Url}";

    if (cur.Price.HasValue && threshold.HasValue && cur.Price <= threshold
        && !(prev!.Price.HasValue && prev.Price <= threshold))
        return $"[Plan {t.Plan}] UNDER YOUR LIMIT (AED {threshold:N0}): {t.Name} at AED {cur.Price:N0}\n{t.Url}";

    return null;
}

async Task<Result> CheckAsync(Target t)
{
    try
    {
        using var resp = await http.GetAsync(t.Url);
        var body = await resp.Content.ReadAsStringAsync();

        if ((int)resp.StatusCode is 403 or 429 or 503 || LooksLikeBotWall(body))
            return new Result("Blocked", null, $"HTTP {(int)resp.StatusCode} / bot protection");
        if (!resp.IsSuccessStatusCode)
            return new Result("Error", null, $"HTTP {(int)resp.StatusCode}");

        return Parse(body);
    }
    catch (Exception ex)
    {
        return new Result("Error", null, ex.GetType().Name + ": " + ex.Message);
    }
}

static bool LooksLikeBotWall(string html)
{
    if (html.Length < 2000 && Regex.IsMatch(html, "captcha|access denied|robot|unusual traffic", RegexOptions.IgnoreCase))
        return true;
    return Regex.IsMatch(html, "api-services-support@amazon|Type the characters you see|Enter the characters you see|_Incapsula_|px-captcha|cf-chl", RegexOptions.IgnoreCase);
}

Result Parse(string html)
{
    // 1) schema.org JSON-LD (most reliable when a shop provides it)
    string? availability = null;
    decimal? ldPrice = null;
    foreach (Match m in Regex.Matches(html, "<script[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>",
                 RegexOptions.Singleline | RegexOptions.IgnoreCase))
    {
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            if (FindOffer(doc.RootElement, out var av, out var pr))
            {
                availability ??= av;
                ldPrice ??= pr;
            }
        }
        catch { /* ignore malformed JSON-LD */ }
    }

    if (ldPrice is { } p0 && (p0 < cfg.MinPriceAed || p0 > cfg.MaxPriceAed)) ldPrice = null;

    if (availability != null)
    {
        var status = availability.Contains("InStock", StringComparison.OrdinalIgnoreCase) ||
                     availability.Contains("LimitedAvailability", StringComparison.OrdinalIgnoreCase) ? "InStock"
                   : availability.Contains("OutOfStock", StringComparison.OrdinalIgnoreCase) ||
                     availability.Contains("SoldOut", StringComparison.OrdinalIgnoreCase) ? "OutOfStock"
                   : availability.Contains("PreOrder", StringComparison.OrdinalIgnoreCase) ? "PreOrder" : "Unknown";
        return new Result(status, ldPrice ?? FindTextPrice(VisibleText(html)), "json-ld");
    }

    // 2) visible text fallback
    var text = VisibleText(html);
    var price = ldPrice ?? FindTextPrice(text);

    if (Regex.IsMatch(text, @"out of stock|sold out|currently unavailable|temporarily unavailable|notify me when", RegexOptions.IgnoreCase))
        return new Result("OutOfStock", price, "text");
    if (Regex.IsMatch(text, @"add to cart|add to basket|buy now|add to bag", RegexOptions.IgnoreCase))
        return new Result("InStock", price, "text");
    return new Result("Unknown", price, "no stock marker found");
}

static bool FindOffer(JsonElement e, out string? availability, out decimal? price)
{
    availability = null; price = null;
    switch (e.ValueKind)
    {
        case JsonValueKind.Object:
            if (e.TryGetProperty("availability", out var av) && av.ValueKind == JsonValueKind.String)
            {
                availability = av.GetString();
                foreach (var key in new[] { "price", "lowPrice" })
                    if (e.TryGetProperty(key, out var pr)) { price = ToDecimal(pr); if (price != null) break; }
                return true;
            }
            foreach (var prop in e.EnumerateObject())
                if (FindOffer(prop.Value, out availability, out price)) return true;
            break;
        case JsonValueKind.Array:
            foreach (var item in e.EnumerateArray())
                if (FindOffer(item, out availability, out price)) return true;
            break;
    }
    return false;
}

static decimal? ToDecimal(JsonElement e) => e.ValueKind switch
{
    JsonValueKind.Number => e.GetDecimal(),
    JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
    _ => null
};

static string VisibleText(string html)
{
    html = Regex.Replace(html, "<(script|style|noscript)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    html = Regex.Replace(html, "<[^>]+>", " ");
    html = WebUtility.HtmlDecode(html);
    return Regex.Replace(html, @"\s+", " ");
}

decimal? FindTextPrice(string text)
{
    // "AED 1,499.00", "D 1,499.00", "Dhs. 1499", "1,499.00 AED" - first value inside the plausible range wins
    // (this skips installment amounts such as "D 125.75/month").
    const string num = @"(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)";
    var hits = new List<(int idx, decimal val)>();
    foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z])(?:AED|Dhs?\.?|D|د\.إ|درهم)\s?" + num))
        if (TryNum(m.Groups[1].Value, out var v)) hits.Add((m.Index, v));
    foreach (Match m in Regex.Matches(text, num + @"\s?(?:AED|Dhs?\b|درهم)"))
        if (TryNum(m.Groups[1].Value, out var v)) hits.Add((m.Index, v));

    foreach (var (_, val) in hits.OrderBy(h => h.idx))
        if (val >= cfg.MinPriceAed && val <= cfg.MaxPriceAed) return val;
    return null;

    static bool TryNum(string s, out decimal v) =>
        decimal.TryParse(s.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
}

void AppendHistory(Target t, Result r)
{
    var newFile = !File.Exists(historyPath);
    var line = string.Join(",", now.ToString("yyyy-MM-dd HH:mm"), Csv(t.Name), t.Plan, r.Status,
        r.Price?.ToString(CultureInfo.InvariantCulture) ?? "", Csv(r.Detail));
    File.AppendAllText(historyPath, (newFile ? "time,store,plan,status,price,detail\n" : "") + line + "\n", Encoding.UTF8);
    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

async Task SendTelegramAsync(string text)
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    var chat = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat)) return;
    try
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chat, ["text"] = text, ["disable_web_page_preview"] = "true"
        });
        var resp = await http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
        Console.WriteLine(resp.IsSuccessStatusCode ? "Telegram alert sent." : $"Telegram failed: HTTP {(int)resp.StatusCode}");
    }
    catch (Exception ex) { Console.WriteLine("Telegram failed: " + ex.Message); }
}

// ======================= types =======================
record Target(string Name, string Plan, string Url);
record Result(string Status, decimal? Price, string Detail);
class Config
{
    public string BlackFriday { get; set; } = "2026-11-27";
    public decimal MinPriceAed { get; set; } = 600;
    public decimal MaxPriceAed { get; set; } = 4000;
    public Dictionary<string, decimal> AlertBelow { get; set; } = new();
    public List<Target> Targets { get; set; } = new();
}