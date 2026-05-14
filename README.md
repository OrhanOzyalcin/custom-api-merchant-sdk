# ExpressAI Custom API — Merchant SDK & Documentation

Bu repo, **ExpressAI Özel Entegrasyon (CUSTOM_API)** tipindeki entegrasyonlar için merchant tarafında uygulanması gereken sözleşmenin **eksiksiz dokümantasyonunu** ve referans **örnek sunucu kodlarını** (Node.js / PHP / C#) içerir.

ExpressAI, kendi pazaryeri entegrasyonu olmayan merchant'ların **kendi sipariş kaynaklarını** ExpressAI'a HTTP üzerinden bağlayabilmesi için bu sözleşmeyi tanımlamıştır. Sözleşmeye uygun iki endpoint sunduğunuzda ExpressAI:

1. Siparişlerinizi periyodik olarak **GET** ile çeker.
2. Statü değişikliklerini (Shipped, Cancelled vb.) size **POST** ile bildirir.

Lokal veritabanımızda statü tutulmaz; **siparişin asıl kaynağı sizsiniz**.

---

## İçindekiler

- [Genel Akış](#genel-akış)
- [Auth — Üç Yöntemden Birini Seçin](#auth--üç-yöntemden-birini-seçin)
- [GET `/api/orders` — Sipariş Listesi](#get-apiorders--sipariş-listesi)
- [POST `/api/status` — Statü Besleme (Batch)](#post-apistatus--statü-besleme-batch)
- [`reason` alanı](#reason-alanı-expressai)
- [`referenceCode` Formatı — 3 Harf + 13 Rakam = 16 Karakter](#referencecode-formatı--3-harf--13-rakam--16-karakter)
- [`status` Enum Değerleri](#status-enum-değerleri)
- [`shipmentAddress` Şeması](#shipmentaddress-şeması)
- [Şehir / İlçe Yardımcı Endpoint'i (public)](#şehir--ilçe-yardımcı-endpointi-public)
- [Idempotency ve Retry Davranışı](#idempotency-ve-retry-davranışı)
- [Hata Davranışı ve Beklenen Durum Kodları](#hata-davranışı-ve-beklenen-durum-kodları)
- [Örnek Sunucu Kodları](#örnek-sunucu-kodları)
- [Postman Koleksiyonu](#postman-koleksiyonu)
- [İletişim](#iletişim)

---

## Genel Akış

```
┌─────────────────┐                                              ┌─────────────────┐
│   ExpressAI     │ ── GET  /api/orders?page=…&pageSize=…  ─→    │   Sizin sunucu  │
│ (sync worker)   │ ←──────  200 OK { page, orders[] } ─────     │  (bu repo)      │
│                 │                                              │                 │
│                 │ ── POST /api/status  { externalOrderId: …} ─→│                 │
│                 │ ←──────  204 No Content ────────────────     │                 │
└─────────────────┘                                              └─────────────────┘
```

ExpressAI Quick Sync'te (yeni siparişler için yüksek frekansta) `status=Created` filtresiyle, Full Sync'te (geçmiş kayıtların eşitlenmesi için düşük frekansta) filtresiz çağrı yapar.

---

## Auth — Üç Yöntemden Birini Seçin

Entegrasyon oluştururken ExpressAI panelinde **kimlik doğrulama yöntemi** olarak üçünden birini seçersiniz. Sizin sunucunuz gelen her istekte aşağıdaki ilgili header'ları doğrulamalıdır.

### 1) API Key + Secret (varsayılan ve önerilen)

```
X-API-Key: <api-key>
X-API-Secret: <api-secret>
```

Uzak sisteminizde iki ayrı uzun rastgele dizgi üretip saklayın.

### 2) HTTP Basic Auth (RFC 7617)

```
Authorization: Basic <base64(user:pass)>
```

Klasik kullanıcı adı / şifre çifti. Sıklıkla mevcut REST API'lerin yanında tercih edilir.

### 3) Bearer Token

```
Authorization: Bearer <token>
```

OAuth2 access token, JWT veya kendi ürettiğiniz statik bir Bearer token kullanabilirsiniz.

**Önemli:** Karşılaştırma yaparken **timing-safe** (constant-time) string karşılaştırma kullanın (Node.js'te `crypto.timingSafeEqual`, PHP'de `hash_equals`, .NET'te `CryptographicOperations.FixedTimeEquals`).

### Web dokümantasyonu ile uyumluluk

ExpressAI **External API Dokümantasyonu** web sayfasında Özel Entegrasyon bölümünde üç kimlik doğrulama yöntemi **ayrı sekmelerde** sunulur; her sekmede ilgili HTTP başlık örnekleri ve panel içi kod snippet'leri o yönteme göre üretilir. Bu repodaki `examples/nodejs`, `examples/php` ve `examples/csharp` projeleri ise aynı üç yöntemi **`AUTH_TYPE` ortam değişkeni** ile çalışma zamanında seçer; panelde seçtiğiniz değerle aynı mantığı takip ederler.

---

## GET `/api/orders` — Sipariş Listesi

### Query Parametreleri

| Parametre | Tür | Açıklama |
|---|---|---|
| `page` | int | 1-indexed sayfa numarası (varsayılan 1) |
| `pageSize` | int | Sayfa başına kayıt (varsayılan 500, maksimum 1000) |
| `status` | string? | Opsiyonel — `MarketplaceOrderStatus` enum değeri ile filtre. Gönderilmezse tüm statüler döner. |

ExpressAI `hasMore=false` dönene kadar `page`'i 1'er artırır.

### Response — 200 OK

```json
{
  "page": 1,
  "pageSize": 500,
  "totalCount": 7234,
  "hasMore": true,
  "orders": [
    {
      "externalOrderId": "ABC-001",
      "orderNumber": "SIP-2026-001",
      "orderDate": "2026-05-14T10:00:00Z",
      "status": "Created",
      "totalPrice": "199.90",
      "referenceCode": "ABC0000000000123",
      "customerName": "Ali Veli",
      "agreedDeliveryDate": "2026-05-20T23:59:59Z",
      "shipmentAddress": {
        "firstName": "Ali",
        "lastName": "Veli",
        "fullName": "Ali Veli",
        "address1": "Atatürk Cad. No:1",
        "city": "İSTANBUL",
        "district": "KADIKÖY",
        "cityId": 34,
        "districtId": 1234,
        "postalCode": "34000",
        "countryCode": "TR",
        "phone": "+905551112233",
        "fullAddress": "Atatürk Cad. No:1, Kadıköy/İstanbul"
      },
      "lines": [
        {
          "id": "L-1",
          "sku": "SKU-123",
          "barcode": "8690000000001",
          "productName": "Örnek Ürün",
          "quantity": 1,
          "amount": "199.90",
          "currencyCode": "TRY"
        }
      ]
    }
  ]
}
```

### Sipariş — Zorunlu Alanlar

| Alan | Açıklama |
|---|---|
| `externalOrderId` | **Sizin sisteminizdeki benzersiz sipariş ID'si.** ExpressAI duplikasyonu bu alanla önler. Unique olmalı. |
| `orderNumber` | Kullanıcıya gösterilecek sipariş numarası |
| `orderDate` | ISO 8601 formatında sipariş tarihi |
| `status` | `MarketplaceOrderStatus` enum değeri (case-sensitive). Bkz. [Status Enum](#status-enum-değerleri) |
| `totalPrice` | Sipariş toplam fiyatı (string veya number) |
| `referenceCode` | **3 harf + 13 rakam = 16 karakter.** Bkz. [Referans Formatı](#referencecode-formatı--3-harf--13-rakam--16-karakter) |
| `customerName` | Müşteri tam adı (UI'da ve kargo etiketinde kullanılır) |
| `shipmentAddress` | Teslimat adresi. Bkz. [shipmentAddress Şeması](#shipmentaddress-şeması) |
| `lines[]` | En az 1 ürün satırı (her satırın zorunlu alanları: `id`, `sku`, `barcode`, `productName`, `quantity`, `amount`, `currencyCode`) |

### Sipariş — Tek Opsiyonel Alan

| Alan | Açıklama |
|---|---|
| `agreedDeliveryDate` | Son kargolanma tarihi (ISO 8601). Belirtilmezse boş bırakın. |

> Yukarıdaki listede yer almayan alanları (örn. `currency`, `realTrackingNumber`, `trackingUrl`, ürün varyantları, satır indirimi vb.) **göndermeyiniz**; ExpressAI kullanmaz.

---

## POST `/api/status` — Statü Besleme (Batch)

ExpressAI sipariş statüsü değiştiğinde size **batch** olarak POST atar. Body'deki her top-level key bir `externalOrderId`'dir; tek bir request'te 1 veya N güncelleme gönderebiliriz.

### Request Body

```json
{
  "ABC-001": {
    "referenceCode": "ABC0000000000123",
    "realTrackingNumber": "TRK987654321",
    "trackingUrl": "https://www.kolaygelsin.com/takip?kod=TRK987654321",
    "status": "Shipped"
  },
  "ABC-002": {
    "referenceCode": "ABC0000000000124",
    "status": "Cancelled",
    "reason": "Order Cancelled"
  },
  "ABC-003": {
    "referenceCode": "ABC0000000000125",
    "realTrackingNumber": "TRK111222333",
    "trackingUrl": "https://www.kolaygelsin.com/takip?kod=TRK111222333",
    "status": "Picking",
    "reason": "Order Created"
  },
  "ABC-004": {
    "status": "Created",
    "referenceCode": "",
    "realTrackingNumber": "",
    "trackingUrl": "",
    "reason": "Cart Changed"
  }
}
```

### Entry — Zorunlu Alanlar

| Alan | Açıklama |
|---|---|
| `status` | Yeni `MarketplaceOrderStatus` değeri (case-sensitive) |

### Entry — Opsiyonel Alanlar

| Alan | Açıklama |
|---|---|
| `referenceCode` | Merchant'ın referans numarası (genellikle ExpressAI sizden gelen değeri geri yollar) |
| `realTrackingNumber` | Gerçek kargo takip numarası (özellikle `Shipped` statüsünde dolu gelir) |
| `trackingUrl` | Müşterinin kargo takibini yapabileceği URL |
| `reason` | ExpressAI'nin olay bağlamı (İngilizce sabit ifadeler). Bkz. [reason alanı](#reason-alanı-expressai). |

Sunucunuz başarılı işlemde **`200 OK`** veya **`204 No Content`** dönmelidir.

---

<a id="reason-alanı-expressai"></a>

## reason alanı — ExpressAI

`reason`, statü POST gövdesindeki her entry içinde **opsiyonel** bir string alanıdır. Siparişin yeni `status` değerinin yanı sıra **bu güncellemenin neden geldiğini** ayırt etmenize yardımcı olur.

| `reason` değeri | Ne anlama gelir? |
|---|---|
| `Order Cancelled` | Sipariş ExpressAI tarafında iptal/iade vb. ile kapatıldı; referans ve takip sıfırlama bildirimi ile birlikte gönderilir (`status` tipik olarak `Cancelled`). |
| `Cart Changed` | Sepet içeriği değiştiği için mevcut iş emrine ait referans/takip sıfırlanıyor; sipariş anahtarı (`externalOrderId`) sizin tarafta aynı kalır (`status` genelde `Created`, ilgili alanlar boş string olabilir). |
| `Order Created` | **Quick Sync** akışında sipariş ilk kez işlenip barkod üretimi başarılı olduktan sonra `Picking` bildirimi yapılırken eklenir (aynı sipariş için yalnızca ilk başarılı bildirimde). |

**Notlar:**

- Kargo takibi veya diğer rutin güncellemelerde `reason` bulunmayabilir; sunucunuz alanı yok saymalı veya isteğe bağlı işlem yapmalıdır.
- İleride yeni sabitler eklenebilir; bilinmeyen bir `reason` için yine de `status` ile idempotent güncelleme yapmanız önerilir.

---

## `referenceCode` Formatı — 3 Harf + 13 Rakam = 16 Karakter

Her siparişin `referenceCode` alanı, **entegrasyon oluşturulurken belirlenen 3 büyük harflik prefix** ile başlamalı ve **13 rakamla** devam etmelidir. Toplam uzunluk her zaman **16 karakter**.

```
^[A-Z]{3}[0-9]{13}$
```

Örnekler:

| Prefix | Geçerli `referenceCode` |
|---|---|
| `ABC` | `ABC0000000000123` |
| `XYZ` | `XYZ9999999999999` |

**Neden 16 karakter?** Kolay Gelsin / SENDEOMP kargosu, IKAS ve TICIMAX entegrasyonlarında da bu 16 karakterlik referans uzunluğunu kullanır. Uzunluk uymayan kayıtlar için gönderi oluşturma adımı başarısız olur.

> Not: SENDEOMP kargosunun `888...` ile başlayan referansı ExpressAI'ın iç kargo prefix'idir; sizin prefix'inizden tamamen bağımsızdır.

---

## `status` Enum Değerleri

| Değer | Açıklama |
|---|---|
| `Created` | Oluşturuldu |
| `Picking` | Hazırlanıyor |
| `Invoiced` | Faturalandı |
| `Shipped` | Kargoya verildi |
| `Cancelled` | İptal edildi |
| `Delivered` | Teslim edildi |
| `UnDelivered` | Teslim edilemedi |
| `Returned` | İade edildi |
| `AtCollectionPoint` | Teslim noktasında |
| `UnPacked` | Paketlenmedi |
| `Awaiting` | Ödeme bekleniyor |
| `UnSupplied` | Tedarik edilemedi |

Değerler **case-sensitive**'dir.

---

## `shipmentAddress` Şeması

```json
{
  "firstName": "Ali",
  "lastName": "Veli",
  "fullName": "Ali Veli",
  "address1": "Atatürk Cad. No:1",
  "address2": "Daire 3",
  "city": "İSTANBUL",
  "district": "KADIKÖY",
  "cityId": 34,
  "districtId": 1234,
  "postalCode": "34000",
  "countryCode": "TR",
  "phone": "+905551112233",
  "fullAddress": "Atatürk Cad. No:1 Daire 3, Kadıköy/İstanbul"
}
```

**Zorunlu alanlar:** `fullName`, `address1`, `city`, `district`, `countryCode`, `phone`.

**Opsiyonel ID alanları (önerilir):** `cityId` ve `districtId`. [Şehir / İlçe yardımcı endpoint'inden](#şehir--ilçe-yardımcı-endpointi-public) okuyup birlikte gönderirseniz Sendeo isim-eşleştirme adımını atlarız; kargo etiketi üretimi daha hızlı ve hatasız olur.

**Diğer opsiyonel:** `firstName / lastName` (yoksa `fullName`'den parse edilir), `address2`, `postalCode`, `fullAddress`.

---

## Şehir / İlçe Yardımcı Endpoint'i (public)

```
GET https://expressai.com.tr/api/marketplace-integrations/custom-api/sendeo-cities
Accept: application/json
```

- **Auth gerektirmez** — herkese açık.
- **Rate limit:** IP başına dakikada 5 istek. Aşılırsa `429 Too Many Requests` + `Retry-After` header'ı dönülür.
- **Veri statik** (TR il / ilçesi). Response'u uzak sisteminizde **1 saat / 1 gün** gibi makul bir süre cache'leyin; her sipariş için yeniden çağırmayın.

### Response — 200 OK

```json
{
  "cities": [
    {
      "cityId": 34,
      "cityName": "İSTANBUL",
      "districts": [
        { "districtId": 1234, "districtName": "KADIKÖY" }
      ]
    }
  ],
  "total": 81,
  "missingCount": 0
}
```

### 503 — Veri Sağlama Servisi Aksaklığı

Cache'de yeterli veri yoksa endpoint `503 Service Unavailable` döner. Bu durum çok nadirdir; karşılaşırsanız ExpressAI Teknik Destek ekibiyle iletişime geçin.

---

## Idempotency ve Retry Davranışı

- **GET `/api/orders`:** Aynı `(page, pageSize, status)` kombinasyonu için her zaman aynı sayfa içeriği dönmelidir. ExpressAI gerektiğinde retry yapabilir.
- **POST `/api/status`:** Yeniden gönderilebilir (retry). `externalOrderId` + `status` birleşimi ile **idempotent davranış sağlayın**: aynı statünün ikinci kez gelmesi sipariş kaydını **bozmamalıdır**.

---

## Hata Davranışı ve Beklenen Durum Kodları

| Durum | Anlamı | Sizin sunucunuzdan beklenen davranış |
|---|---|---|
| `200` / `204` | Başarılı | İşlem tamamlandı |
| `400` | Kötü istek | Anlamlı bir hata mesajı + body |
| `401` | Yetkisiz | Auth bilgileri yanlış / eksik |
| `429` | Rate limit (varsa) | `Retry-After` header'ı önerilir |
| `5xx` | Sunucu hatası | ExpressAI loglar ve sonraki sync'te tekrar dener |

Hata cevaplarında body örneği:

```json
{
  "error": "Invalid status value",
  "details": { "expected": "MarketplaceOrderStatus enum", "received": "shipped" }
}
```

---

## Örnek Sunucu Kodları

Üç popüler dil için referans gerçekleştirimleri:

- [`examples/nodejs/server.js`](./examples/nodejs/server.js) — Express.js minimal sunucu
- [`examples/php/server.php`](./examples/php/server.php) — Vanilla PHP (Slim ile uyumlu)
- [`examples/csharp/Program.cs`](./examples/csharp/Program.cs) — ASP.NET Core Minimal API

Her örnek üç auth yöntemini de gösterir; sizin tercih ettiğiniz auth bloğunu aktif tutup diğerlerini silebilirsiniz.

---

## Postman Koleksiyonu

ExpressAI panelinde entegrasyonunuzu oluşturduktan sonra **"API Dokümantasyonu" > "Postman'a Aktar"** butonu ile size özel hazır bir Postman koleksiyonu (v2.1) indirebilirsiniz. Bu koleksiyon:

- Sizin endpoint URL'lerinizle önceden doldurulmuştur,
- Seçtiğiniz auth tipine göre header'ları içerir,
- Quick Sync / Full Sync GET istekleri + POST Statü Besleme + Sendeo Cities GET olmak üzere 4 örnek request içerir.

---

## İletişim

- **Web:** https://expressai.com.tr
- **Teknik Destek:** ExpressAI panelinden destek talebi oluşturabilirsiniz.

Bu repo MIT lisansı altında dağıtılır.
