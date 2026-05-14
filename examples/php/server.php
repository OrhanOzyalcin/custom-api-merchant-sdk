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
    $referenceCode = $prefix . str_pad((string)$o['sequence'], 13, '0', STR_PAD_LEFT);
    if (!preg_match('/^[A-Z]{3}[0-9]{13}$/', $referenceCode)) {
        throw new RuntimeException("Invalid referenceCode: {$referenceCode}");
    }
    $out = [
        'externalOrderId' => $o['id'],
        'orderNumber' => $o['publicNumber'],
        'orderDate' => $o['createdAt'],
        'status' => $o['status'],
        'totalPrice' => number_format((float)$o['total'], 2, '.', ''),
        'referenceCode' => $referenceCode,
        'customerName' => $o['customerFullName'],
        'shipmentAddress' => [
            'fullName' => $o['customerFullName'],
            'address1' => $o['address1'],
            'city' => $o['cityName'],
            'district' => $o['districtName'],
            'cityId' => $o['cityId'],
            'districtId' => $o['districtId'],
            'countryCode' => 'TR',
            'phone' => $o['phone'],
        ],
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
    if (!empty($o['agreedDeliveryDate'])) {
        $out['agreedDeliveryDate'] = $o['agreedDeliveryDate'];
    }
    return $out;
}

function load_orders_from_db(?string $statusFilter): array
{
    $sample = [[
        'id' => 'ABC-001',
        'publicNumber' => 'SIP-2026-001',
        'createdAt' => '2026-05-14T10:00:00Z',
        'status' => 'Created',
        'total' => 199.90,
        'sequence' => 123,
        'customerFullName' => 'Ali Veli',
        'agreedDeliveryDate' => '2026-05-20T23:59:59Z',
        'address1' => 'Atatürk Cad. No:1',
        'cityName' => 'İSTANBUL',
        'districtName' => 'KADIKÖY',
        'cityId' => 34,
        'districtId' => 1234,
        'phone' => '+905551112233',
        'lines' => [
            ['id' => 'L-1', 'sku' => 'SKU-123', 'barcode' => '8690000000001',
             'name' => 'Örnek Ürün', 'qty' => 1, 'price' => 199.90],
        ],
    ]];
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
