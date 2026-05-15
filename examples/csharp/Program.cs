/**
 * ExpressAI Custom API — ASP.NET Core Minimal API referans sunucu.
 *
 * Üç auth yöntemini de içerir; AUTH_TYPE env değişkeniyle aktif olan seçilir:
 *   AUTH_TYPE=API_KEY_SECRET  -> X-API-Key + X-API-Secret
 *   AUTH_TYPE=BASIC_AUTH      -> Authorization: Basic base64(user:pass)
 *   AUTH_TYPE=BEARER_TOKEN    -> Authorization: Bearer <token>
 *
 * Gereksinim: .NET 8.0+.
 *
 * Çalıştırma:
 *   AUTH_TYPE=API_KEY_SECRET EXPRESSAI_API_KEY=... EXPRESSAI_API_SECRET=... \
 *   REFERENCE_PREFIX=ABC dotnet run
 */

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var REFERENCE_PREFIX = Environment.GetEnvironmentVariable("REFERENCE_PREFIX") ?? "ABC";
var REFERENCE_REGEX = new Regex(@"^[A-Z]{3}[0-9]{13}$", RegexOptions.Compiled);
var AUTH_TYPE = Environment.GetEnvironmentVariable("AUTH_TYPE") ?? "API_KEY_SECRET";

// =========================================================
// 1) GET /api/orders
// =========================================================
app.MapGet("/api/orders", async (HttpRequest req) =>
{
    if (!IsAuthorized(req, AUTH_TYPE)) return Results.Unauthorized();

    int page = int.TryParse(req.Query["page"], out var p) ? Math.Max(1, p) : 1;
    int pageSize = int.TryParse(req.Query["pageSize"], out var ps)
        ? Math.Clamp(ps, 1, 1000)
        : 500;
    string? statusFilter = req.Query["status"];
    if (string.IsNullOrEmpty(statusFilter)) statusFilter = null;

    var all = await LoadOrdersFromDb(statusFilter);
    int totalCount = all.Count;
    int start = (page - 1) * pageSize;
    var slice = all.Skip(start).Take(pageSize).ToList();

    return Results.Json(new
    {
        page,
        pageSize,
        totalCount,
        hasMore = start + slice.Count < totalCount,
        orders = slice.Select(o => MapOrderToExpressAi(o, REFERENCE_PREFIX, REFERENCE_REGEX))
    });
});

// =========================================================
// 2) POST /api/status (batch)
// Her entry: status (+ opsiyonel referenceCode, realTrackingNumber, trackingUrl, reason)
// =========================================================
app.MapPost("/api/status", async (HttpRequest req) =>
{
    if (!IsAuthorized(req, AUTH_TYPE)) return Results.Unauthorized();

    using var doc = await JsonDocument.ParseAsync(req.Body);
    if (doc.RootElement.ValueKind != JsonValueKind.Object)
    {
        return Results.BadRequest(new { error = "Body must be an object keyed by externalOrderId" });
    }

    foreach (var entry in doc.RootElement.EnumerateObject())
    {
        if (!entry.Value.TryGetProperty("status", out var statusProp)
            || statusProp.ValueKind != JsonValueKind.String)
        {
            return Results.BadRequest(new { error = $"Missing 'status' for {entry.Name}" });
        }
        await UpdateOrderStatus(entry.Name, entry.Value);
    }
    return Results.NoContent();
});

Console.WriteLine($"Listening on :3000 (AUTH_TYPE={AUTH_TYPE}, PREFIX={REFERENCE_PREFIX})");
app.Run("http://0.0.0.0:3000");

// =========================================================
// Auth
// =========================================================

static bool IsAuthorized(HttpRequest req, string authType)
{
    if (authType == "API_KEY_SECRET")
    {
        var key = req.Headers["X-API-Key"].ToString();
        var secret = req.Headers["X-API-Secret"].ToString();
        return SafeEqual(Environment.GetEnvironmentVariable("EXPRESSAI_API_KEY") ?? "", key)
            && SafeEqual(Environment.GetEnvironmentVariable("EXPRESSAI_API_SECRET") ?? "", secret);
    }

    if (authType == "BASIC_AUTH")
    {
        var header = req.Headers["Authorization"].ToString();
        if (!header.StartsWith("Basic ")) return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
        }
        catch (FormatException)
        {
            return false;
        }
        var parts = decoded.Split(':', 2);
        if (parts.Length != 2) return false;
        return SafeEqual(Environment.GetEnvironmentVariable("EXPRESSAI_USERNAME") ?? "", parts[0])
            && SafeEqual(Environment.GetEnvironmentVariable("EXPRESSAI_PASSWORD") ?? "", parts[1]);
    }

    if (authType == "BEARER_TOKEN")
    {
        var header = req.Headers["Authorization"].ToString();
        var token = header.StartsWith("Bearer ") ? header[7..] : "";
        return !string.IsNullOrEmpty(token)
            && SafeEqual(Environment.GetEnvironmentVariable("EXPRESSAI_BEARER_TOKEN") ?? "", token);
    }

    return false;
}

// Constant-time karşılaştırma — timing attack'lara karşı koruma sağlar.
static bool SafeEqual(string a, string b)
{
    var ab = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    if (ab.Length != bb.Length) return false;
    return CryptographicOperations.FixedTimeEquals(ab, bb);
}

// =========================================================
// Mapping
// =========================================================

// Opsiyonel customDeciWeight: pozitif JSON number (Sendeo desi/kg). Yoksa veya <= 0 ise alan gönderilmez; ExpressAI packageDesi kullanır.
static Dictionary<string, object?> BuildShipmentAddress(Order o)
{
    var d = new Dictionary<string, object?>
    {
        ["fullName"] = o.CustomerFullName,
        ["address1"] = o.Address1,
        ["city"] = o.CityName,
        ["district"] = o.DistrictName,
        ["cityId"] = o.CityId,
        ["districtId"] = o.DistrictId,
        ["countryCode"] = "TR",
        ["phone"] = o.Phone,
    };
    if (o.CustomDeciWeight is decimal w && w > 0m)
        d["customDeciWeight"] = (double)w;
    return d;
}

static object MapOrderToExpressAi(Order o, string prefix, Regex referenceRegex)
{
    string referenceCode = prefix + o.Sequence.ToString().PadLeft(13, '0');
    if (!referenceRegex.IsMatch(referenceCode))
    {
        throw new InvalidOperationException($"Invalid referenceCode: {referenceCode}");
    }
    return new
    {
        externalOrderId = o.Id,
        orderNumber = o.PublicNumber,
        orderDate = o.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        status = o.Status,
        totalPrice = o.Total.ToString("0.00"),
        cargoProvider = "KolayGelsin",
        referenceCode,
        customerName = o.CustomerFullName,
        agreedDeliveryDate = o.AgreedDeliveryDate?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        shipmentAddress = BuildShipmentAddress(o),
        lines = o.Lines.Select(li => new
        {
            id = li.Id,
            sku = li.Sku,
            barcode = li.Barcode,
            productName = li.Name,
            quantity = li.Qty,
            amount = li.Price.ToString("0.00"),
            currencyCode = "TRY"
        })
    };
}

// =========================================================
// Mock veri / DB layer — kendi DB'nize bağlayın
// =========================================================

static Task<List<Order>> LoadOrdersFromDb(string? statusFilter)
{
    var sample = new List<Order>
    {
        new(
            Id: "ABC-001",
            PublicNumber: "SIP-2026-001",
            CreatedAt: new DateTime(2026, 5, 14, 10, 0, 0, DateTimeKind.Utc),
            Status: "Created",
            Total: 199.90m,
            Sequence: 123,
            CustomerFullName: "Ali Veli",
            AgreedDeliveryDate: new DateTime(2026, 5, 20, 23, 59, 59, DateTimeKind.Utc),
            Address1: "Atatürk Cad. No:1",
            CityName: "İSTANBUL",
            DistrictName: "KADIKÖY",
            CityId: 34,
            DistrictId: 1234,
            Phone: "+905551112233",
            CustomDeciWeight: 2.5m,
            Lines: new List<Line>
            {
                new(Id: "L-1", Sku: "SKU-123", Barcode: "8690000000001",
                    Name: "Örnek Ürün", Qty: 1, Price: 199.90m)
            }
        )
    };
    return Task.FromResult(
        statusFilter is null
            ? sample
            : sample.Where(o => o.Status == statusFilter).ToList()
    );
}

// ÖNEMLİ: idempotent davranış sağlayın (aynı externalOrderId + status tekrarında bozulma olmasın).
// Opsiyonel reason (JSON): ExpressAI olay bağlamı — Order Cancelled, Cart Changed, Order Created.
static Task UpdateOrderStatus(string externalOrderId, JsonElement payload)
{
    Console.WriteLine($"[status update] {externalOrderId} -> {payload.GetRawText()}");
    return Task.CompletedTask;
}

// =========================================================
// Tipler
// =========================================================

record Order(
    string Id, string PublicNumber, DateTime CreatedAt, string Status,
    decimal Total, long Sequence, string CustomerFullName,
    DateTime? AgreedDeliveryDate, string Address1, string CityName,
    string DistrictName, int CityId, int DistrictId, string Phone,
    List<Line> Lines,
    decimal? CustomDeciWeight = null);

record Line(string Id, string Sku, string Barcode, string Name, int Qty, decimal Price);
