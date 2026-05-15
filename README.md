# ExpressAI Custom API — Merchant SDK & Documentation

Bu repo, **ExpressAI Özel Entegrasyon (CUSTOM_API)** tipindeki entegrasyonlar için merchant tarafında uygulanması gereken sözleşmenin **eksiksiz dokümantasyonunu** ve referans **örnek sunucu kodlarını** (Node.js / PHP / C#) içerir.

ExpressAI, kendi pazaryeri entegrasyonu olmayan merchant'ların **kendi sipariş kaynaklarını** ExpressAI'a HTTP üzerinden bağlayabilmesi için bu sözleşmeyi tanımlamıştır. Sözleşmeye uygun iki endpoint sunduğunuzda ExpressAI:

1. Siparişlerinizi periyodik olarak **GET** ile çeker.
2. Statü değişikliklerini (Shipped, Cancelled, UnDelivered, Returned vb.; bazılarında `reason` bağlamı ile) size **POST** ile bildirir.

Lokal veritabanımızda statü tutulmaz; **siparişin asıl kaynağı sizsiniz**.

---

## ExpressAI panelinde entegrasyon

Özel API entegrasyonunu oluşturup yapılandırmak (endpoint URL’leri, kimlik doğrulama ve panel ayarları) için ExpressAI’da **[Express AI → Entegrasyonlar](https://expressai.com.tr/final-label/integrations)** sayfasını kullanın.

---

## İçindekiler

- [ExpressAI panelinde entegrasyon](#expressai-panelinde-entegrasyon)
- [Genel Akış](#genel-akış)
- [Auth — Üç Yöntemden Birini Seçin](#auth--üç-yöntemden-birini-seçin)
- [GET `/api/orders` — Sipariş Listesi](#get-apiorders--sipariş-listesi)
- [`referenceCode` — ExpressAI üretimi ve GET’te opsiyonellik](#referencecode-formatı--expressai-tarafından-üretilen-kargo-referansı)
- [POST `/api/status` — Statü Besleme (Batch)](#post-apistatus--statü-besleme-batch)
- [`reason` alanı](#reason-alanı-expressai)
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
      "cargoProvider": "KolayGelsin",
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
        "fullAddress": "Atatürk Cad. No:1, Kadıköy/İstanbul",
        "customDeciWeight": 2.5
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

**Not:** Örnek gövdede `referenceCode` gösterilmiştir; GET sipariş yanıtında bu alan **zorunlu değildir**. Kalıcı referansı ExpressAI, panelde tanımlı `referenceCodePrefix` ile üretir ve saklar. `shipmentAddress.customDeciWeight` **tamamen opsiyoneldir**; yoksa Sendeo desi için entegrasyon `packageDesi` kullanılır.

**Kargo filtresi:** Her siparişte `cargoProvider` tam olarak **`KolayGelsin`** olmalıdır (büyük-küçük harf duyarlı). Başka kargo seçilmiş siparişleri GET listesinden çıkarın; yanlış veya eksik `cargoProvider` ile gelen kayıtlar içe aktarılmaz.

### Sipariş — Zorunlu Alanlar

| Alan | Açıklama |
|---|---|
| `externalOrderId` | **Sizin sisteminizdeki benzersiz sipariş ID'si.** ExpressAI duplikasyonu bu alanla önler. Unique olmalı. |
| `orderNumber` | Kullanıcıya gösterilecek sipariş numarası |
| `orderDate` | ISO 8601 formatında sipariş tarihi |
| `status` | `MarketplaceOrderStatus` enum değeri (case-sensitive). Bkz. [Status Enum](#status-enum-değerleri) |
| `totalPrice` | Sipariş toplam fiyatı (string veya number) |
| `cargoProvider` | **Zorunlu:** tam olarak `KolayGelsin` (büyük-küçük harf duyarlı). ExpressAI yalnızca bu değere sahip siparişleri içe aktarır; diğer kargoları listeden çıkarın. |
| `customerName` | Müşteri tam adı (UI'da ve kargo etiketinde kullanılır) |
| `shipmentAddress` | Teslimat adresi. Bkz. [shipmentAddress Şeması](#shipmentaddress-şeması) |
| `lines[]` | En az 1 ürün satırı (her satırın zorunlu alanları: `id`, `sku`, `barcode`, `productName`, `quantity`, `amount`, `currencyCode`) |

### Sipariş — Opsiyonel Alanlar

| Alan | Açıklama |
|---|---|
| `agreedDeliveryDate` | Son kargolanma tarihi (ISO 8601). Belirtilmezse boş bırakın. |
| `referenceCode` | **Tamamen opsiyonel.** Gönderseniz bile kalıcı referans ExpressAI tarafında `referenceCodePrefix` ile üretilir; merchant’tan gelen değer saklamada kullanılmaz (bilgi amaçlı olabilir). Gönderirseniz format kurallarına uyun; bkz. [Referans formatı](#referencecode-formatı--expressai-tarafından-üretilen-kargo-referansı). |

> Yukarıdaki tablolarda yer almayan alanları (örn. `currency`, `realTrackingNumber`, `trackingUrl`, ürün varyantları, satır indirimi vb.) **göndermeyiniz**; ExpressAI kullanmaz.

---

## `referenceCode` formatı — ExpressAI tarafından üretilen kargo referansı

Kalıcı gönderi referansı (**16 karakter:** entegrasyon **prefix’i 3 büyük harf** + **13 rakam**) ExpressAI tarafında atanır ve Kolay Gelsin / SENDEOMP gönderi oluşturma ile uyumludur.

```
^[A-Z]{3}[0-9]{13}$
```

Örnekler:

| Prefix | Üretilen referansa örnek |
|---|---|
| `ABC` | `ABC0000000000123` |
| `XYZ` | `XYZ9999999999999` |

**GET `/api/orders` içinde merchant `referenceCode` gönderirse:** İsteğe bağlı doğrulamada (ExpressAI panel doğrulama akışı) değer **prefix ile başlamalı** ve yukarıdaki regex ile **16 karakter** olmalıdır; aksi halde doğrulama uyarısı/ reddi oluşabilir.

**Neden 16 karakter?** IKAS ve TICIMAX entegrasyonlarıyla aynı uzunluk sözleşmesidir; SENDEOMP gönderi oluşturma pipeline’ı bu uzunluğa bağlıdır.

> SENDEOMP tarafında görülen `888...` ile başlayan bazı referanslar ExpressAI iç kargo prefix’idir; sizin merchant prefix’inizden bağımsızdır.

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
    "status": "Cancelled",
    "referenceCode": "",
    "realTrackingNumber": "",
    "trackingUrl": "",
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
  },
  "ABC-005": {
    "referenceCode": "ABC0000000000123",
    "realTrackingNumber": "TRK444555666",
    "trackingUrl": "https://www.kolaygelsin.com/takip?kod=TRK444555666",
    "status": "UnDelivered",
    "reason": "Carrier Undelivered"
  },
  "ABC-006": {
    "referenceCode": "ABC0000000000125",
    "realTrackingNumber": "TRK998877665",
    "trackingUrl": "https://www.kolaygelsin.com/takip?kod=TRK998877665",
    "status": "Returned",
    "reason": "Return Requested"
  },
  "ABC-007": {
    "referenceCode": "ABC0000000000125",
    "realTrackingNumber": "TRK998877665",
    "trackingUrl": "https://www.kolaygelsin.com/takip?kod=TRK998877665",
    "status": "Returned",
    "reason": "Return Approved"
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
| `referenceCode` | ExpressAI’nin atadığı gönderi referans kodu (ör. barkod sonrası Picking bildiriminde). |
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
| `Order Cancelled` | Sipariş **iptal** edildiğinde gönderilir (`status`: `Cancelled`). **`referenceCode` / `realTrackingNumber` / `trackingUrl` boş string** olarak gelir; merchant tarafında eski referans ve takip bilgisini temizlemeniz beklenir (Cart Changed sıfırlaması ile aynı şablon). |
| `Cart Changed` | Sepet içeriği değiştiği için referans/takip sıfırlanıyor (`status` genelde `Created`). **`referenceCode` / `realTrackingNumber` / `trackingUrl` boş string** — yeni referans, sonraki barkod başarılı olduktan sonra ayrı bir POST ile gelir. |
| `Order Created` | **Quick Sync** akışında sipariş ilk kez işlenip barkod üretimi başarılı olduktan sonra `Picking` bildirimi yapılırken eklenir (aynı sipariş için yalnızca ilk başarılı bildirimde). |
| `Carrier Undelivered` | Kolay Gelsin `GetCargoList` yanıtında `lastStatusId === 130` (Teslim Edilemedi) olduğunda **Tam Senkron** ile ExpressAI siparişi `UnDelivered` olarak bildirirken eklenir; takip alanları KG'den doluysa aynı POST içinde gelir (SENDEOMP credentials gerekir). |
| `Return Requested` | KG `lastStatusId === 113` (İade Talebi) iken `Returned` statüsü POST edilirken eklenir. |
| `Return Approved` | KG `lastStatusId === 134` (İade Onay) iken `Returned` statüsü POST edilirken eklenir. |

**Not:** Müşteri iadesi (`Returned`) ile sipariş **iptali** (`Cancelled` + `Order Cancelled`) farklı akışlardır; POST gövdesindeki `status` ve `reason` birlikte değerlendirilmelidir.

**Notlar:**

- Çoğu rutin güncellemede `reason` bulunmayabilir; sunucunuz alanı yok saymalı veya isteğe bağlı işlem yapmalıdır.
- İleride yeni sabitler eklenebilir; bilinmeyen bir `reason` için yine de `status` ile idempotent güncelleme yapmanız önerilir.

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
  "fullAddress": "Atatürk Cad. No:1 Daire 3, Kadıköy/İstanbul",
  "customDeciWeight": 2.5
}
```

**Zorunlu alanlar:** `fullName`, `address1`, `city`, `district`, `countryCode`, `phone`.

**Opsiyonel ID alanları (önerilir):** `cityId` ve `districtId`. [Şehir / İlçe yardımcı endpoint'inden](#şehir--ilçe-yardımcı-endpointi-public) okuyup birlikte gönderirseniz Sendeo isim-eşleştirme adımını atlarız; kargo etiketi üretimi daha hızlı ve hatasız olur.

**Diğer opsiyonel:** `firstName / lastName` (yoksa `fullName`'den parse edilir), `address2`, `postalCode`, `fullAddress`, `customDeciWeight` (isteğe bağlı pozitif **JSON sayısı** — Sendeo satırındaki desi/kg; göndermezseniz veya geçersizse ExpressAI entegrasyon `packageDesi` kullanılır. **String sayı göndermeyin;** içe aktarım yalnızca `number` tipini okur.)

**Örnek sunucular (`examples/nodejs`, `examples/php`, `examples/csharp`) ve içe aktarım:** Bu üç örnek, ana projedeki `lib/custom-api-order-sync.ts` içindeki `convertCustomApiOrderToMarketplaceOrder` + `buildCustomApiShipmentAddress` ile uyumlu **minimum + önerilen** alanları gösterir. Üstteki geniş JSON örneğindeki `firstName` / `postalCode` / `fullAddress` gibi alanları örnek sunucular göndermez; ExpressAI bunları `fullName` ve adres satırlarından türetir veya varsayılanlarla tamamlar. **%100 birebir alan listesi** beklenmez; kritik olan sipariş düzeyinde `cargoProvider: KolayGelsin`, dolu `lines`, ve adreste en azından geçerli `address1` + `city` (Sendeo çözümü için) ile birlikte gönderilen kimliklerdir.

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
