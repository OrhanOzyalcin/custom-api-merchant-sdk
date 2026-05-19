<?php
/**
 * ExpressAI Custom API — Vanilla PHP referans sunucu.
 *
 * Üç auth yöntemini de içerir; AUTH_TYPE env değişkeniyle aktif olan seçilir:
 *   AUTH_TYPE=API_KEY_SECRET  -> X-API-Key + X-API-Secret
 *   AUTH_TYPE=BASIC_AUTH      -> Authorization: Basic base64(user:pass)
 *   AUTH_TYPE=BEARER_TOKEN    -> Authorization: Bearer <token>
 *
 * Çalıştırma (geliştirme):
 *   AUTH_TYPE=API_KEY_SECRET \
 *   EXPRESSAI_API_KEY=... EXPRESSAI_API_SECRET=... \
 *   REFERENCE_PREFIX=ABC \
 *   php -S 0.0.0.0:3000 server.php
 *
 * Üretim: nginx + php-fpm önerilir. Slim Framework'e taşımak da kolaydır.
 *
 * İçe aktarım kuralları:
 *   - status `Awaiting` KABUL EDİLMEZ. ExpressAI Awaiting (ödeme/onay bekleyen) siparişleri içeri almaz;
 *     netleşmemiş/belirsiz olduklarından paketleme aşamasına geçmemelidir. GET yanıtına dahil etmeyin;
 *     dahil edilirse Zod enum reddi ile sessizce atlanır. Bu örnekte Awaiting kayıt yoktur.
 *   - shipmentAddress.phone KOŞULLU:
 *       isMarketplace=false (kendi sipariş, ExpressAI delivery pipeline) -> ZORUNLU + E.164 formatı
 *         (^\+[1-9][0-9]{10,14}$, Türkiye: +905XXXXXXXXX). Eksik/geçersizse sipariş atlanır.
 *       isMarketplace=true (pazaryeri) -> KABUL EDİLMEZ; merchant gönderse bile DB'ye yazılmaz
 *         (pazaryeri müşteri iletişimini kendi platformunda yönetir, KVKK/GDPR yüzeyi küçültülür).
 */

declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');

$REFERENCE_PREFIX = getenv('REFERENCE_PREFIX') ?: 'ABC';
$AUTH_TYPE = getenv('AUTH_TYPE') ?: 'API_KEY_SECRET';

// ----- Auth -----

/**
 * Constant-time karşılaştırma için hash_equals kullanır.
 * 401 yanıt vererek script'i sonlandırır.
 */
function require_auth(string $authType): void
{
    if ($authType === 'API_KEY_SECRET') {
        $key = $_SERVER['HTTP_X_API_KEY'] ?? '';
        $secret = $_SERVER['HTTP_X_API_SECRET'] ?? '';
        if (
            !hash_equals((string)(getenv('EXPRESSAI_API_KEY') ?: ''), $key)
            || !hash_equals((string)(getenv('EXPRESSAI_API_SECRET') ?: ''), $secret)
        ) {
            respond_error(401, 'Unauthorized');
        }
        return;
    }

    if ($authType === 'BASIC_AUTH') {
        $header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
        if (strpos($header, 'Basic ') !== 0) {
            respond_error(401, 'Unauthorized');
        }
        $decoded = base64_decode(substr($header, 6), true) ?: '';
        $parts = explode(':', $decoded, 2);
        $user = $parts[0] ?? '';
        $pass = $parts[1] ?? '';
        if (
            !hash_equals((string)(getenv('EXPRESSAI_USERNAME') ?: ''), $user)
            || !hash_equals((string)(getenv('EXPRESSAI_PASSWORD') ?: ''), $pass)
        ) {
            respond_error(401, 'Unauthorized');
        }
        return;
    }

    if ($authType === 'BEARER_TOKEN') {
        $header = $_SERVER['HTTP_AUTHORIZATION'] ?? '';
        $token = (strpos($header, 'Bearer ') === 0) ? substr($header, 7) : '';
        if ($token === '' || !hash_equals((string)(getenv('EXPRESSAI_BEARER_TOKEN') ?: ''), $token)) {
            respond_error(401, 'Unauthorized');
        }
        return;
    }

    respond_error(500, 'AUTH_TYPE misconfigured');
}

function respond_error(int $status, string $message): never
{
    http_response_code($status);
    echo json_encode(['error' => $message], JSON_UNESCAPED_UNICODE);
    exit;
}

// ----- Router -----

$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
$method = $_SERVER['REQUEST_METHOD'];

require_auth($AUTH_TYPE);

// =========================================================
// 1) GET /api/orders
// =========================================================
if ($method === 'GET' && $path === '/api/orders') {
    $page = max(1, (int)($_GET['page'] ?? 1));
    $pageSize = min(1000, max(1, (int)($_GET['pageSize'] ?? 500)));
    $statusFilter = $_GET['status'] ?? null;

    $allOrders = load_orders_from_db($statusFilter);
    $totalCount = count($allOrders);
    $start = ($page - 1) * $pageSize;
    $slice = array_slice($allOrders, $start, $pageSize);

    echo json_encode([
        'page' => $page,
        'pageSize' => $pageSize,
        'totalCount' => $totalCount,
        'hasMore' => $start + count($slice) < $totalCount,
        'orders' => array_map(
            fn(array $o) => map_order_to_expressai($o, $REFERENCE_PREFIX),
            $slice
        ),
    ], JSON_UNESCAPED_UNICODE);
    exit;
}

// =========================================================
// 2) POST /api/status (batch)
//    Her entry: status (+ opsiyonel referenceCode, realTrackingNumber, trackingUrl, reason)
// =========================================================
if ($method === 'POST' && $path === '/api/status') {
    $raw = file_get_contents('php://input') ?: '';
    $body = json_decode($raw, true);
    if (!is_array($body)) {
        respond_error(400, 'Body must be a JSON object keyed by externalOrderId');
    }
    foreach ($body as $externalOrderId => $payload) {
        if (!is_array($payload) || !is_string($payload['status'] ?? null)) {
            respond_error(400, "Missing 'status' for {$externalOrderId}");
        }
        update_order_status((string)$externalOrderId, $payload);
    }
    http_response_code(204);
    exit;
}

respond_error(404, 'Not Found');

// =========================================================
// Yardımcı fonksiyonlar — kendi DB'nize bağlayın
// =========================================================

function map_order_to_expressai(array $o, string $prefix): array
{
    // isMarketplace: zorunlu boolean.
    //   true  = sipariş bir DIŞ pazaryerinden (Trendyol/HB/N11/IKAS/Ticimax) gelmiştir; ExpressAI
    //           SetDelivery ATMAZ, statü besleme POST etmez, KG GetCargoList sync atlanır — yalnızca arşivler.
    //   false = ExpressAI delivery pipeline'ında merchant'ın kendi siparişidir; ExpressAI Sendeo SetDelivery
    //           (Kolay Gelsin) ile gönderi açar, barkod alır, statü besleme POST eder, KG sync çalıştırır.
    // Backward-compat: alan yoksa varsayılan false (kendi sipariş — pipeline işler).
    $isMarketplace = array_key_exists('isMarketplace', $o)
        ? (bool)$o['isMarketplace']
        : false;

    // cargoProvider: marketplace-cargo-data.ts cargoProviders[].name listesinden serbest değer
    // (Kolay Gelsin, HepsiJet, Yurtiçi Kargo, ...). Varsayılan "Kolay Gelsin" (kendi sipariş — Sendeo için).
    $cargoProvider = (isset($o['cargoProvider']) && is_string($o['cargoProvider']) && trim($o['cargoProvider']) !== '')
        ? trim($o['cargoProvider'])
        : 'Kolay Gelsin';

    // referenceCode:
    //   isMarketplace=false (kendi sipariş) → prefix + 13 hane (16 karakter, IKAS/TICIMAX uyumlu);
    //   isMarketplace=true (pazaryeri)      → merchant'ın pazaryerinden aldığı gerçek tracking numarası
    //                                          (serbest format, 16 karakter regex muafiyeti).
    // referenceCode kargo sağlayıcı tarafında karşılık bulan anahtardır; `realTrackingNumber` ile
    // karıştırılmamalıdır — realTrackingNumber yalnızca SetDelivery / KG sync sonucunda dolar.
    if (!$isMarketplace) {
        $referenceCode = $prefix . str_pad((string)$o['sequence'], 13, '0', STR_PAD_LEFT);
        if (!preg_match('/^[A-Z]{3}[0-9]{13}$/', $referenceCode)) {
            throw new RuntimeException("Invalid referenceCode: {$referenceCode}");
        }
    } else {
        $referenceCode = (isset($o['referenceCode']) && is_string($o['referenceCode']))
            ? $o['referenceCode']
            : '';
    }

    // shipmentAddress.phone koşullu:
    //   !$isMarketplace -> zorunlu + E.164 formatı; doğruluğu merchant tarafında garanti edilmeli.
    //   $isMarketplace  -> output'a dahil etme (merchant gönderse bile ExpressAI DB'ye yazmaz).
    $shipmentAddress = [
        'fullName' => $o['customerFullName'],
        'address1' => $o['address1'],
        'city' => $o['cityName'],
        'district' => $o['districtName'],
        'cityId' => $o['cityId'],
        'districtId' => $o['districtId'],
        'countryCode' => 'TR',
    ];
    if (!$isMarketplace && isset($o['phone']) && is_string($o['phone']) && trim($o['phone']) !== '') {
        $shipmentAddress['phone'] = trim($o['phone']);
    }
    if (
        isset($o['customDeciWeight'])
        && is_numeric($o['customDeciWeight'])
        && (float)$o['customDeciWeight'] > 0
    ) {
        $shipmentAddress['customDeciWeight'] = (float)$o['customDeciWeight'];
    }

    $out = [
        'externalOrderId' => $o['id'],
        'orderNumber' => $o['publicNumber'],
        'orderDate' => $o['createdAt'],
        'status' => $o['status'],
        'totalPrice' => number_format((float)$o['total'], 2, '.', ''),
        'isMarketplace' => $isMarketplace,
        'cargoProvider' => $cargoProvider,
        'referenceCode' => $referenceCode,
        'customerName' => $o['customerFullName'],
        'shipmentAddress' => $shipmentAddress,
        'lines' => array_map(fn(array $li) => [
            'id' => $li['id'],
            'sku' => $li['sku'],
            'barcode' => $li['barcode'],
            'productName' => $li['name'],
            'quantity' => $li['qty'],
            'amount' => number_format((float)$li['price'], 2, '.', ''),
            'currencyCode' => 'TRY',
        ], $o['lines']),
    ];

    // marketPlaceName: isMarketplace=true (pazaryeri) ise zorunlu — Trendyol | Hepsiburada | N11 | IKAS | Ticimax.
    // isMarketplace=false (kendi sipariş) durumunda alanı gönderme (ExpressAI yok sayar / null saklar).
    if ($isMarketplace && isset($o['marketPlaceName']) && is_string($o['marketPlaceName']) && trim($o['marketPlaceName']) !== '') {
        $out['marketPlaceName'] = trim($o['marketPlaceName']);
    }

    if (!empty($o['agreedDeliveryDate'])) {
        $out['agreedDeliveryDate'] = $o['agreedDeliveryDate'];
    }
    return $out;
}

function load_orders_from_db(?string $statusFilter): array
{
    $sample = [
        [
            // MOCK-001: merchant'ın kendi siparişi (isMarketplace=false — ExpressAI delivery pipeline).
            // ExpressAI Sendeo SetDelivery (Kolay Gelsin) ile gönderi açar, referenceCode panel prefix'i ile üretilir,
            // statü besleme POST'ları (Picking, Cart Changed, Order Cancelled vb.) merchant'a iletilir,
            // KG GetCargoList ile teslim/iade/iptal statüleri tam senkronla güncellenir.
            'id' => 'ABC-001',
            'publicNumber' => 'SIP-2026-001',
            'createdAt' => '2026-05-14T10:00:00Z',
            'status' => 'Created',
            'total' => 199.90,
            'sequence' => 123,
            'isMarketplace' => false,
            'cargoProvider' => 'Kolay Gelsin',
            'customerFullName' => 'Ali Veli',
            'agreedDeliveryDate' => '2026-05-20T23:59:59Z',
            'address1' => 'Atatürk Cad. No:1',
            'cityName' => 'İSTANBUL',
            'districtName' => 'KADIKÖY',
            'cityId' => 34,
            'districtId' => 1234,
            'phone' => '+905551112233',
            // Opsiyonel: Sendeo desi/kg (JSON number). Key'i kaldırırsanız ExpressAI packageDesi kullanır.
            'customDeciWeight' => 2.5,
            'lines' => [
                ['id' => 'L-1', 'sku' => 'SKU-123', 'barcode' => '8690000000001',
                 'name' => 'Örnek Ürün', 'qty' => 1, 'price' => 199.90],
            ],
        ],
        [
            // MOCK-002: pazaryeri siparişi (Trendyol, isMarketplace=true).
            // ExpressAI bu kaydı yalnızca arşivler: SetDelivery, statü besleme ve KG tam senkron ATLANIR.
            // cargoProvider serbest (Yurtiçi Kargo); referenceCode pazaryeri tracking numarası (16 karakter regex muafiyeti).
            'id' => 'ABC-002',
            'publicNumber' => 'SIP-2026-002',
            'createdAt' => '2026-05-14T11:30:00Z',
            'status' => 'Created',
            'total' => 349.00,
            'sequence' => 124,
            'isMarketplace' => true,
            'marketPlaceName' => 'Trendyol',
            'cargoProvider' => 'Yurtiçi Kargo',
            'referenceCode' => 'TRK987654321',
            'customerFullName' => 'Ayşe Kaya',
            'address1' => 'İnönü Cad. No:42',
            'cityName' => 'ANKARA',
            'districtName' => 'ÇANKAYA',
            'cityId' => 6,
            'districtId' => 932,
            // Pazaryeri (isMarketplace=true) — phone alanı kasıtlı olarak yok; merchant gönderse bile
            // ExpressAI DB'ye yazmaz, map_order_to_expressai output'a dahil etmez.
            'lines' => [
                ['id' => 'L-2', 'sku' => 'SKU-456', 'barcode' => '8690000000002',
                 'name' => 'Pazaryeri Ürünü', 'qty' => 1, 'price' => 349.00],
            ],
        ],
    ];
    if ($statusFilter !== null) {
        return array_values(array_filter($sample, fn($o) => $o['status'] === $statusFilter));
    }
    return $sample;
}

function update_order_status(string $externalOrderId, array $payload): void
{
    // ÖNEMLİ: idempotent davranış sağlayın!
    // Opsiyonel $payload['reason']: ExpressAI olay bağlamı (örn. Order Cancelled, Cart Changed, Order Created).
    // Aynı (externalOrderId, status) ikinci kez geldiğinde sipariş bozulmamalı.
    error_log("[status update] {$externalOrderId} -> " . json_encode($payload, JSON_UNESCAPED_UNICODE));
    // Örn:
    //   $stmt = $pdo->prepare("UPDATE orders SET status = ?, tracking_no = ? WHERE external_order_id = ?");
    //   $stmt->execute([$payload['status'], $payload['realTrackingNumber'] ?? null, $externalOrderId]);
}
