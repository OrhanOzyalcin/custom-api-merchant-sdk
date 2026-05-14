# C# / ASP.NET Core örneği

Üç auth yöntemi `AUTH_TYPE` ortam değişkeni ile seçilir; web dokümantasyonunda Özel Entegrasyon her yöntem için ayrı sekmede gösterilir.

ASP.NET Core 8.0 Minimal API ile yazılmış referans sunucu.

## Gereksinim

- .NET SDK 8.0 veya üstü.

## Çalıştırma

```bash
# API Key + Secret
AUTH_TYPE=API_KEY_SECRET \
EXPRESSAI_API_KEY=your-api-key \
EXPRESSAI_API_SECRET=your-api-secret \
REFERENCE_PREFIX=ABC \
dotnet run
```

```bash
# HTTP Basic Auth
AUTH_TYPE=BASIC_AUTH \
EXPRESSAI_USERNAME=your-username \
EXPRESSAI_PASSWORD=your-password \
REFERENCE_PREFIX=ABC \
dotnet run
```

```bash
# Bearer Token
AUTH_TYPE=BEARER_TOKEN \
EXPRESSAI_BEARER_TOKEN=your-bearer-token \
REFERENCE_PREFIX=ABC \
dotnet run
```

Sunucu `http://localhost:3000` üzerinde başlar:

- `GET  /api/orders?page=1&pageSize=500&status=Created`
- `POST /api/status` — Batch gövde; `reason` dahil opsiyonel alanlar için [README — reason alanı](../../README.md#reason-alanı-expressai).

`LoadOrdersFromDb` ve `UpdateOrderStatus` fonksiyonlarını kendi veritabanınıza bağlayın (EF Core, Dapper, raw SQL vb.).
