# PHP örneği

Üç auth yöntemi `AUTH_TYPE` ortam değişkeni ile seçilir; web dokümantasyonunda Özel Entegrasyon her yöntem için ayrı sekmede gösterilir.

Vanilla PHP ile yazılmış, harici bağımlılığı olmayan referans sunucu. Slim Framework veya Symfony'ye taşımak kolaydır. Slim Framework veya Symfony'ye taşımak kolaydır.

## Gereksinim

- PHP 8.1+ (`declare(strict_types=1)` ve `never` return type için)

## Çalıştırma

Geliştirme amaçlı PHP built-in sunucu:

```bash
# API Key + Secret
AUTH_TYPE=API_KEY_SECRET \
EXPRESSAI_API_KEY=your-api-key \
EXPRESSAI_API_SECRET=your-api-secret \
REFERENCE_PREFIX=ABC \
php -S 0.0.0.0:3000 server.php
```

```bash
# HTTP Basic Auth
AUTH_TYPE=BASIC_AUTH \
EXPRESSAI_USERNAME=your-username \
EXPRESSAI_PASSWORD=your-password \
REFERENCE_PREFIX=ABC \
php -S 0.0.0.0:3000 server.php
```

```bash
# Bearer Token
AUTH_TYPE=BEARER_TOKEN \
EXPRESSAI_BEARER_TOKEN=your-bearer-token \
REFERENCE_PREFIX=ABC \
php -S 0.0.0.0:3000 server.php
```

Sunucu `http://localhost:3000` üzerinde başlar:

- `GET  /api/orders?page=1&pageSize=500&status=Created`
- `POST /api/status` — Batch gövde; `reason` dahil opsiyonel alanlar için [README — reason alanı](../../README.md#reason-alanı-expressai).

## Üretim

Apache veya nginx + php-fpm önerilir. Apache için `.htaccess` ile tüm istekleri `server.php`'ye yönlendirebilirsiniz; nginx'te `try_files` ile aynısı yapılır.

`load_orders_from_db` ve `update_order_status` fonksiyonlarını kendi veritabanınıza bağlayın (PDO, Doctrine, Eloquent vb.).
