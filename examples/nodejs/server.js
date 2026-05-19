/**
 * ExpressAI Custom API — Node.js / Express referans sunucu.
 *
 * Üç auth yöntemini de içerir; AUTH_TYPE env değişkeniyle aktif olan seçilir:
 *   AUTH_TYPE=API_KEY_SECRET  -> X-API-Key + X-API-Secret
 *   AUTH_TYPE=BASIC_AUTH      -> Authorization: Basic base64(user:pass)
 *   AUTH_TYPE=BEARER_TOKEN    -> Authorization: Bearer <token>
 *
 * Gereksinim: Node.js 18+ (built-in fetch ve crypto).
 *   npm install express
 *   AUTH_TYPE=API_KEY_SECRET EXPRESSAI_API_KEY=... EXPRESSAI_API_SECRET=... node server.js
 *
 * İçe aktarım kuralları:
 *   - status `Awaiting` KABUL EDİLMEZ. ExpressAI Awaiting (ödeme/onay bekleyen) siparişleri içeri almaz;
 *     netleşmemiş/belirsiz olduklarından paketleme aşamasına geçmemelidir. GET yanıtına dahil etmeyin;
 *     dahil edilirse Zod enum reddi ile sessizce atlanır. Bu mock'ta hiç Awaiting örnek yoktur.
 *   - shipmentAddress.phone KOŞULLU:
 *       isMarketplace=false (kendi sipariş, ExpressAI delivery pipeline) → ZORUNLU + E.164 formatı
 *         (^\+[1-9][0-9]{10,14}$, Türkiye: +905XXXXXXXXX). Eksik/geçersizse sipariş atlanır.
 *       isMarketplace=true (pazaryeri) → KABUL EDİLMEZ; merchant gönderse bile DB'ye yazılmaz
 *         (pazaryeri müşteri iletişimini kendi platformunda yönetir, KVKK/GDPR yüzeyi küçültülür).
 */

import express from "express";
import crypto from "crypto";

const REFERENCE_PREFIX = process.env.REFERENCE_PREFIX || "ABC"; // 3 büyük harf
const REFERENCE_REGEX = new RegExp(`^[A-Z]{3}[0-9]{13}$`);
const AUTH_TYPE = process.env.AUTH_TYPE || "API_KEY_SECRET";

const app = express();
app.use(express.json({ limit: "2mb" }));

// ---- Constant-time string karşılaştırma (timing-safe) ----
function safeEqual(a, b) {
  const ab = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ab.length !== bb.length) return false;
  return crypto.timingSafeEqual(ab, bb);
}

// ---- Auth middleware (AUTH_TYPE'a göre branch) ----
function requireAuth(req, res, next) {
  if (AUTH_TYPE === "API_KEY_SECRET") {
    const key = req.header("X-API-Key") || "";
    const secret = req.header("X-API-Secret") || "";
    if (
      !safeEqual(process.env.EXPRESSAI_API_KEY || "", key) ||
      !safeEqual(process.env.EXPRESSAI_API_SECRET || "", secret)
    ) {
      return res.status(401).json({ error: "Unauthorized" });
    }
  } else if (AUTH_TYPE === "BASIC_AUTH") {
    const header = req.header("Authorization") || "";
    if (!header.startsWith("Basic ")) {
      return res.status(401).json({ error: "Unauthorized" });
    }
    const decoded = Buffer.from(header.slice(6), "base64").toString("utf-8");
    const [user, pass = ""] = decoded.split(":");
    if (
      !safeEqual(process.env.EXPRESSAI_USERNAME || "", user || "") ||
      !safeEqual(process.env.EXPRESSAI_PASSWORD || "", pass)
    ) {
      return res.status(401).json({ error: "Unauthorized" });
    }
  } else if (AUTH_TYPE === "BEARER_TOKEN") {
    const header = req.header("Authorization") || "";
    const token = header.startsWith("Bearer ") ? header.slice(7) : "";
    if (!token || !safeEqual(process.env.EXPRESSAI_BEARER_TOKEN || "", token)) {
      return res.status(401).json({ error: "Unauthorized" });
    }
  } else {
    return res.status(500).json({ error: "AUTH_TYPE misconfigured" });
  }
  next();
}

// =========================================================
// 1) GET /api/orders — ExpressAI buradan siparişleri çeker.
//    Quick Sync: ?status=Created
//    Full Sync:  status parametresi yok
// =========================================================
app.get("/api/orders", requireAuth, async (req, res) => {
  const page = Math.max(1, parseInt(req.query.page, 10) || 1);
  const pageSize = Math.min(1000, Math.max(1, parseInt(req.query.pageSize, 10) || 500));
  const statusFilter = typeof req.query.status === "string" ? req.query.status : null;

  // Kendi DB'nizden siparişleri çekin. Burada in-memory mock veri:
  const allOrders = await loadOrdersFromDb({ statusFilter });
  const totalCount = allOrders.length;
  const start = (page - 1) * pageSize;
  const slice = allOrders.slice(start, start + pageSize);

  res.json({
    page,
    pageSize,
    totalCount,
    hasMore: start + slice.length < totalCount,
    orders: slice.map((o) => mapOrderToExpressAi(o)),
  });
});

// =========================================================
// 2) POST /api/status — ExpressAI statü değişikliklerini buraya bildirir (batch).
//    Body: { [externalOrderId]: { status, referenceCode?, realTrackingNumber?, trackingUrl?, reason? } }
// =========================================================
app.post("/api/status", requireAuth, async (req, res) => {
  const body = req.body || {};
  if (typeof body !== "object" || Array.isArray(body)) {
    return res.status(400).json({ error: "Body must be an object keyed by externalOrderId" });
  }
  for (const [externalOrderId, payload] of Object.entries(body)) {
    if (!payload || typeof payload.status !== "string") {
      return res.status(400).json({
        error: `Missing 'status' for ${externalOrderId}`,
      });
    }
    await updateOrderStatus(externalOrderId, payload);
  }
  res.status(204).end();
});

app.listen(3000, () => {
  console.log(`Listening on :3000 (AUTH_TYPE=${AUTH_TYPE}, PREFIX=${REFERENCE_PREFIX})`);
});

// =========================================================
// Yardımcı fonksiyonlar
// =========================================================

function mapOrderToExpressAi(o) {
  // isMarketplace: zorunlu boolean.
  //   true  = sipariş bir DIŞ pazaryerinden (Trendyol/HB/N11/IKAS/Ticimax) gelmiştir; ExpressAI SetDelivery
  //           ATMAZ, statü besleme POST etmez, KG GetCargoList sync atlanır — yalnızca arşivler.
  //   false = ExpressAI delivery pipeline'ında merchant'ın kendi siparişidir; ExpressAI Sendeo SetDelivery
  //           (Kolay Gelsin) ile gönderi açar, barkod alır, statü besleme POST eder, KG sync çalıştırır.
  // Backward-compat: alan yoksa varsayılan false (kendi sipariş — pipeline işler).
  const isMarketplace = typeof o.isMarketplace === "boolean" ? o.isMarketplace : false;

  // cargoProvider: marketplace-cargo-data.ts cargoProviders[].name listesinden serbest değer.
  // Varsayılan "Kolay Gelsin" (kendi sipariş — Sendeo SetDelivery için);
  // pazaryeri senaryosunda merchant'tan gelen gerçek kargo adı (örn. "Yurtiçi Kargo") kullanılır.
  const cargoProvider = typeof o.cargoProvider === "string" && o.cargoProvider.trim()
    ? o.cargoProvider.trim()
    : "Kolay Gelsin";

  // referenceCode üretimi yalnızca isMarketplace=false (kendi sipariş — ExpressAI delivery pipeline) için yapılır.
  // isMarketplace=true (pazaryeri) durumunda merchant'ın pazaryerinden aldığı gerçek tracking numarası
  // olduğu gibi gönderilir; bu kargo sağlayıcı tarafında karşılık bulan anahtardır ve `realTrackingNumber`
  // ile karıştırılmamalıdır (realTrackingNumber yalnızca SetDelivery / KG sync sonucunda dolar).
  let referenceCode;
  if (!isMarketplace) {
    referenceCode = `${REFERENCE_PREFIX}${String(o.sequence).padStart(13, "0")}`;
    if (!REFERENCE_REGEX.test(referenceCode)) {
      throw new Error(`Invalid referenceCode generated: ${referenceCode}`);
    }
  } else {
    referenceCode = typeof o.referenceCode === "string" ? o.referenceCode : "";
  }

  /** @type {Record<string, unknown>} */
  const out = {
    externalOrderId: o.id,
    orderNumber: o.publicNumber,
    orderDate: o.createdAt.toISOString(),
    status: o.status,
    totalPrice: o.total.toFixed(2),
    isMarketplace,
    cargoProvider,
    referenceCode,
    customerName: o.customerFullName,
  };

  // marketPlaceName: yalnızca isMarketplace=true (pazaryeri) durumunda zorunlu — pazaryeri adı
  // (Trendyol | Hepsiburada | N11 | IKAS | Ticimax). isMarketplace=false (kendi sipariş) ise alanı gönderme.
  if (isMarketplace && typeof o.marketPlaceName === "string" && o.marketPlaceName.trim()) {
    out.marketPlaceName = o.marketPlaceName.trim();
  }

  if (o.agreedDeliveryDate) {
    out.agreedDeliveryDate = o.agreedDeliveryDate.toISOString();
  }

  out.shipmentAddress = {
    fullName: o.customerFullName,
    address1: o.address1,
    city: o.cityName,          // örn. "İSTANBUL"
    district: o.districtName,  // örn. "KADIKÖY"
    cityId: o.cityId,          // opsiyonel ama önerilir
    districtId: o.districtId,  // opsiyonel ama önerilir
    countryCode: "TR",
    // phone koşullu: isMarketplace=false ise ZORUNLU + E.164 (^\+[1-9][0-9]{10,14}$);
    // isMarketplace=true ise KABUL EDİLMEZ (merchant gönderse bile DB'ye yazılmaz) — gönderme.
    ...(!isMarketplace && typeof o.phone === "string" && o.phone.trim()
      ? { phone: o.phone.trim() }
      : {}),
    // Opsiyonel: pozitif JSON number (Sendeo desi/kg). Yoksa veya <= 0 ise ExpressAI entegrasyon packageDesi kullanır.
    ...(typeof o.customDeciWeight === "number" && o.customDeciWeight > 0
      ? { customDeciWeight: o.customDeciWeight }
      : {}),
  };

  out.lines = o.lines.map((li) => ({
    id: li.id,
    sku: li.sku,
    barcode: li.barcode,
    productName: li.name,
    quantity: li.qty,
    amount: li.price.toFixed(2),
    currencyCode: "TRY",
  }));

  return out;
}

// Kendi DB'nize bağlayın (Prisma, Sequelize, raw SQL vb.).
// Burada deterministik iki örnek sipariş döndürüyoruz: biri merchant'ın kendi siparişi (isMarketplace=false),
// diğeri pazaryeri siparişi (isMarketplace=true + marketPlaceName + serbest cargoProvider + serbest referenceCode).
async function loadOrdersFromDb({ statusFilter }) {
  const sample = [
    {
      // MOCK-001: merchant'ın kendi siparişi (isMarketplace=false — ExpressAI delivery pipeline).
      // ExpressAI Sendeo SetDelivery (Kolay Gelsin) ile gönderi açar, referenceCode panel prefix'i ile üretilir,
      // statü besleme POST'ları (Picking, Cart Changed, Order Cancelled vb.) yapılır, KG GetCargoList sync çalışır.
      id: "ABC-001",
      publicNumber: "SIP-2026-001",
      createdAt: new Date("2026-05-14T10:00:00Z"),
      status: "Created",
      total: 199.9,
      sequence: 123,
      isMarketplace: false,
      cargoProvider: "Kolay Gelsin",
      customerFullName: "Ali Veli",
      agreedDeliveryDate: new Date("2026-05-20T23:59:59Z"),
      address1: "Atatürk Cad. No:1",
      cityName: "İSTANBUL",
      districtName: "KADIKÖY",
      cityId: 34,
      districtId: 1234,
      phone: "+905551112233",
      // Opsiyonel: alanı kaldırabilir veya null yapabilirsiniz — o zaman packageDesi kullanılır.
      customDeciWeight: 2.5,
      lines: [
        {
          id: "L-1",
          sku: "SKU-123",
          barcode: "8690000000001",
          name: "Örnek Ürün",
          qty: 1,
          price: 199.9,
        },
      ],
    },
    {
      // MOCK-002: pazaryeri siparişi (Trendyol, isMarketplace=true) — ExpressAI bunu yalnızca arşivler;
      // SetDelivery / statü besleme / KG tam senkron tamamen ATLANIR. cargoProvider serbest (örn. Yurtiçi Kargo).
      // referenceCode: pazaryerinden gelen gerçek tracking numarası (serbest format, 16 karakter regex muafiyeti).
      id: "ABC-002",
      publicNumber: "SIP-2026-002",
      createdAt: new Date("2026-05-14T11:30:00Z"),
      status: "Created",
      total: 349.0,
      sequence: 124,
      isMarketplace: true,
      marketPlaceName: "Trendyol",
      cargoProvider: "Yurtiçi Kargo",
      referenceCode: "TRK987654321",
      customerFullName: "Ayşe Kaya",
      address1: "İnönü Cad. No:42",
      cityName: "ANKARA",
      districtName: "ÇANKAYA",
      cityId: 6,
      districtId: 932,
      // Pazaryeri (isMarketplace=true) — phone alanı kasıtlı olarak yok; merchant gönderse bile
      // ExpressAI DB'ye yazmaz, mapOrderToExpressAi de output'a dahil etmez.
      lines: [
        {
          id: "L-2",
          sku: "SKU-456",
          barcode: "8690000000002",
          name: "Pazaryeri Ürünü",
          qty: 1,
          price: 349.0,
        },
      ],
    },
  ];
  if (statusFilter) {
    return sample.filter((o) => o.status === statusFilter);
  }
  return sample;
}

// Kendi DB'nize bağlayın. ÖNEMLİ: idempotent davranış sağlayın!
// Aynı (externalOrderId, status) ikinci kez geldiğinde sipariş bozulmamalı.
// Opsiyonel payload.reason: ExpressAI olay bağlamı ("Order Cancelled" | "Cart Changed" | "Order Created").
async function updateOrderStatus(externalOrderId, payload) {
  console.log("[status update]", externalOrderId, payload);
  // Örnek:
  //   await prisma.order.update({
  //     where: { externalOrderId },
  //     data: {
  //       status: payload.status,
  //       trackingNumber: payload.realTrackingNumber ?? undefined,
  //       trackingUrl: payload.trackingUrl ?? undefined,
  //     }
  //   });
}
