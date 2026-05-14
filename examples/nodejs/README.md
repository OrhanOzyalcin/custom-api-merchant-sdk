# Node.js / Express örneği

Üç kimlik doğrulama yöntemi (`API_KEY_SECRET`, `BASIC_AUTH`, `BEARER_TOKEN`) tek kod tabanında `AUTH_TYPE` ile seçilir; ExpressAI web dokümantasyonunda Özel Entegrasyon için her yöntem ayrı sekmede de anlatılır.

## Kurulum

```bash
npm install
```

## Çalıştırma

Auth tipinize göre ortam değişkenlerini ayarlayın:

```bash
# API Key + Secret
AUTH_TYPE=API_KEY_SECRET \
EXPRESSAI_API_KEY=your-api-key \
EXPRESSAI_API_SECRET=your-api-secret \
REFERENCE_PREFIX=ABC \
npm start
```

```bash
# HTTP Basic Auth
AUTH_TYPE=BASIC_AUTH \
EXPRESSAI_USERNAME=your-username \
EXPRESSAI_PASSWORD=your-password \
REFERENCE_PREFIX=ABC \
npm start
```

```bash
# Bearer Token
AUTH_TYPE=BEARER_TOKEN \
EXPRESSAI_BEARER_TOKEN=your-bearer-token \
REFERENCE_PREFIX=ABC \
npm start
```

Sunucu `http://localhost:3000` üzerinde başlar; iki endpoint ile cevap verir:

- `GET  /api/orders?page=1&pageSize=500&status=Created`
- `POST /api/status` — JSON batch; her entry'de `status` zorunlu; `referenceCode` / `realTrackingNumber` / `trackingUrl` / **`reason`** opsiyonel. Açıklama: repo kökündeki [README — reason alanı](../../README.md#reason-alanı-expressai).

`loadOrdersFromDb` ve `updateOrderStatus` fonksiyonlarını kendi veritabanınıza bağlayın.
