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
 *
 * İçe aktarım kuralları:
 *   - status `Awaiting` KABUL EDİLMEZ. ExpressAI Awaiting (ödeme/onay bekleyen) siparişleri içeri almaz;
 *     netleşmemiş/belirsiz olduklarından paketleme aşamasına geçmemelidir. GET yanıtına dahil etmeyin;
 *     dahil edilirse Zod enum reddi ile sessizce atlanır. Bu örnekte Awaiting kayıt yoktur.
 *   - shipmentAddress.phone KOŞULLU:
 *       IsMarketplace=false (kendi sipariş, ExpressAI delivery pipeline) -> ZORUNLU + E.164 formatı
 *         (^\+[1-9][0-9]{10,14}$, Türkiye: +905XXXXXXXXX). Eksik/geçersizse sipariş atlanır.
 *       IsMarketplace=true (pazaryeri) -> KABUL EDİLMEZ; merchant gönderse bile DB'ye yazılmaz
 *         (pazaryeri müşteri iletişimini kendi platformunda yönetir, KVKK/GDPR yüzeyi küçültülür).
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

// shipmentAddress.phone koşullu:
//   IsMarketplace=false -> zorunlu + E.164 formatı (^\+[1-9][0-9]{10,14}$, Türkiye: +905XXXXXXXXX);
//                          eksik/geçersizse sipariş atlanır.
//   IsMarketplace=true  -> output'a dahil etme (merchant gönderse bile ExpressAI DB'ye yazmaz).
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
    };
    if (!o.IsMarketplace && !string.IsNullOrWhiteSpace(o.Phone))
        d["phone"] = o.Phone.Trim();
    if (o.CustomDeciWeight is decimal w && w > 0m)
        d["customDeciWeight"] = (double)w;
    return d;
}

static object MapOrderToExpressAi(Order o, string prefix, Regex referenceRegex)
{
    // isMarketplace: zorunlu boolean.
    //   true  = sipariş bir DIŞ pazaryerinden (Trendyol/HB/N11/IKAS/Ticimax) gelmiştir; ExpressAI
    //           SetDelivery ATMAZ, statü besleme POST etmez, KG GetCargoList sync atlanır — yalnızca arşivler.
    //   false = ExpressAI delivery pipeline'ında merchant'ın kendi siparişidir; ExpressAI Sendeo SetDelivery
    //           (Kolay Gelsin) ile gönderi açar, barkod alır, statü besleme POST eder, KG sync çalıştırır.
    bool isMarketplace = o.IsMarketplace;

    // cargoProvider: marketplace-cargo-data.ts cargoProviders[].name listesinden serbest değer
    // (Kolay Gelsin, HepsiJet, Yurtiçi Kargo, ...). Default "Kolay Gelsin" — kendi sipariş Sendeo SetDelivery için.
    string cargoProvider = string.IsNullOrWhiteSpace(o.CargoProvider) ? "Kolay Gelsin" : o.CargoProvider.Trim();

    // referenceCode:
    //   isMarketplace=false (kendi sipariş) → prefix + 13 hane (16 karakter, IKAS/TICIMAX uyumlu);
    //   isMarketplace=true  (pazaryeri)     → merchant'ın pazaryeri tracking numarası (serbest format).
    // referenceCode kargo sağlayıcı tarafında karşılık bulan anahtardır; `realTrackingNumber` ile
    // karıştırılmamalıdır — realTrackingNumber yalnızca SetDelivery / KG sync sonucunda dolar.
    string referenceCode;
    if (!isMarketplace)
    {
        referenceCode = prefix + o.Sequence.ToString().PadLeft(13, '0');
        if (!referenceRegex.IsMatch(referenceCode))
        {
            throw new InvalidOperationException($"Invalid referenceCode: {referenceCode}");
        }
    }
    else
    {
        referenceCode = o.ReferenceCode ?? "";
    }

    // marketPlaceName: yalnızca isMarketplace=true (pazaryeri) durumunda zorunlu. false (kendi sipariş) durumunda
    // alanı göndermeyiz (ExpressAI yok sayar / null saklar).
    string? marketPlaceName = (isMarketplace && !string.IsNullOrWhiteSpace(o.MarketPlaceName))
        ? o.MarketPlaceName.Trim()
        : null;

    var payload = new Dictionary<string, object?>
    {
        ["externalOrderId"] = o.Id,
        ["orderNumber"] = o.PublicNumber,
        ["orderDate"] = o.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["status"] = o.Status,
        ["totalPrice"] = o.Total.ToString("0.00"),
        ["isMarketplace"] = isMarketplace,
        ["cargoProvider"] = cargoProvider,
        ["referenceCode"] = referenceCode,
        ["customerName"] = o.CustomerFullName,
    };
    if (marketPlaceName != null)
    {
        payload["marketPlaceName"] = marketPlaceName;
    }
    if (o.AgreedDeliveryDate is DateTime adt)
    {
        payload["agreedDeliveryDate"] = adt.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
    payload["shipmentAddress"] = BuildShipmentAddress(o);
    payload["lines"] = o.Lines.Select(li => new
    {
        id = li.Id,
        sku = li.Sku,
        barcode = li.Barcode,
        productName = li.Name,
        quantity = li.Qty,
        amount = li.Price.ToString("0.00"),
        currencyCode = "TRY"
    });
    return payload;
}

// =========================================================
// Mock veri / DB layer — kendi DB'nize bağlayın
// =========================================================

static Task<List<Order>> LoadOrdersFromDb(string? statusFilter)
{
    var sample = new List<Order>
    {
        // MOCK-001: merchant'ın kendi siparişi (isMarketplace=false — ExpressAI delivery pipeline).
        // ExpressAI Sendeo SetDelivery (Kolay Gelsin) ile gönderi açar; referenceCode panel prefix'i ile üretilir;
        // statü besleme POST'ları (Picking, Cart Changed, Order Cancelled vb.) merchant'a iletilir;
        // KG GetCargoList ile teslim/iade/iptal statüleri tam senkronla güncellenir.
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
            IsMarketplace: false,
            CargoProvider: "Kolay Gelsin",
            CustomDeciWeight: 2.5m,
            Lines: new List<Line>
            {
                new(Id: "L-1", Sku: "SKU-123", Barcode: "8690000000001",
                    Name: "Örnek Ürün", Qty: 1, Price: 199.90m)
            }
        ),
        // MOCK-002: pazaryeri siparişi (Trendyol, isMarketplace=true).
        // ExpressAI bu kaydı yalnızca arşivler: SetDelivery / statü besleme POST'ları / KG tam senkron ATLANIR.
        // cargoProvider serbest (örn. Yurtiçi Kargo); referenceCode pazaryeri tracking numarası (16 karakter regex muafiyeti).
        new(
            Id: "ABC-002",
            PublicNumber: "SIP-2026-002",
            CreatedAt: new DateTime(2026, 5, 14, 11, 30, 0, DateTimeKind.Utc),
            Status: "Created",
            Total: 349.00m,
            Sequence: 124,
            CustomerFullName: "Ayşe Kaya",
            AgreedDeliveryDate: null,
            Address1: "İnönü Cad. No:42",
            CityName: "ANKARA",
            DistrictName: "ÇANKAYA",
            CityId: 6,
            DistrictId: 932,
            // Pazaryeri (IsMarketplace=true) — Phone null; merchant gönderse bile ExpressAI DB'ye yazmaz,
            // BuildShipmentAddress output JSON'una dahil etmez.
            Phone: null,
            IsMarketplace: true,
            CargoProvider: "Yurtiçi Kargo",
            MarketPlaceName: "Trendyol",
            ReferenceCode: "TRK987654321",
            Lines: new List<Line>
            {
                new(Id: "L-2", Sku: "SKU-456", Barcode: "8690000000002",
                    Name: "Pazaryeri Ürünü", Qty: 1, Price: 349.00m)
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

// Order record — yeni alanlar (geriye uyumlu varsayılan değerlerle):
//   IsMarketplace: zorunlu boolean (default false = merchant'ın kendi siparişi — ExpressAI delivery pipeline).
//                  true = dış pazaryeri siparişi (Trendyol/HB/N11/IKAS/Ticimax) — ExpressAI yalnızca arşivler.
//   CargoProvider: cargoProviders[].name listesinden serbest değer (default "Kolay Gelsin" — Sendeo için).
//   MarketPlaceName: yalnızca IsMarketplace=true için zorunlu (Trendyol|Hepsiburada|N11|IKAS|Ticimax).
//   ReferenceCode: IsMarketplace=true durumunda merchant'ın pazaryeri tracking numarası (serbest format);
//                  false durumunda ExpressAI panel prefix'i ile üretilir.
//   Phone: KOŞULLU. IsMarketplace=false ise zorunlu + E.164 formatı (^\+[1-9][0-9]{10,14}$);
//                   IsMarketplace=true ise null/boş bırakın — BuildShipmentAddress output'a dahil etmez.
record Order(
    string Id, string PublicNumber, DateTime CreatedAt, string Status,
    decimal Total, long Sequence, string CustomerFullName,
    DateTime? AgreedDeliveryDate, string Address1, string CityName,
    string DistrictName, int CityId, int DistrictId, string? Phone,
    List<Line> Lines,
    bool IsMarketplace = false,
    string CargoProvider = "Kolay Gelsin",
    string? MarketPlaceName = null,
    string? ReferenceCode = null,
    decimal? CustomDeciWeight = null);

record Line(string Id, string Sku, string Barcode, string Name, int Qty, decimal Price);
