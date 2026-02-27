# OsmondLocalApi

Windows Service (.NET 8, x64) that exposes a local-only HTTP API to read Algerian biometric ID cards and passports through Adaptive Recognition `pr-sdk-2.2` with Osmond-R-V2 USB reader.

## API

### POST `/read`
- URL: `http://127.0.0.1:8765/read`
- Optional header: `X-API-Key` (required only when configured in appsettings)

Response format:

```json
{
  "ok": true,
  "code": "",
  "message": "Read completed.",
  "fields": {
    "full_name_ar": "",
    "full_name_lat": "",
    "dob": "",
    "sex": "",
    "doc_no": "",
    "nin": "",
    "address": "",
    "issue_date": "",
    "expiry_date": ""
  },
  "raw": {
    "mrz": "",
    "barcode": ""
  },
  "images": {
    "photo_base64": "",
    "photo_mime": "image/jpeg"
  }
}
```

## Configuration

Create/edit:

`%ProgramData%\OsmondLocalApi\appsettings.json`

```json
{
  "port": 8765,
  "timeoutSeconds": 10,
  "includePhoto": true,
  "deviceName": "Osmond R V2 SN1234",
  "apiKey": "optional-api-key"
}
```

Logs are written to:

`%ProgramData%\OsmondLocalApi\logs`

## Publish (self-contained x64)

```powershell
dotnet restore
dotnet publish .\OsmondLocalApi.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\publish
```

## Register as Windows Service (sc.exe)

> Run elevated PowerShell/CMD.

```powershell
sc.exe create OsmondLocalApi binPath= "C:\Program Files\OsmondLocalApi\OsmondLocalApi.exe" start= auto
sc.exe description OsmondLocalApi "Local API for Algerian eID/passport read via Osmond R V2"
sc.exe start OsmondLocalApi
```

To update service binary:

```powershell
sc.exe stop OsmondLocalApi
# replace binary
sc.exe start OsmondLocalApi
```

To remove:

```powershell
sc.exe stop OsmondLocalApi
sc.exe delete OsmondLocalApi
```

## Integration notes (pr-sdk-2.2)

`Services/OsmondReaderService.cs` includes the exact orchestration points expected by production integration:
- `AuthBegin`
- `AuthWaitForInput`
- `AuthFinished`
- await `ReadFinished(FileId.All)` via `TaskCompletionSource`
- always perform chip authentication and chip DG read
- image selection priority `DG2` face then fallback to VIZ face

Replace the `PrSdkGateway` placeholder with your concrete SDK calls based on installed SDK API signatures.

## Usage examples

### PHP (cURL)

```php
<?php
$ch = curl_init('http://127.0.0.1:8765/read');
curl_setopt($ch, CURLOPT_POST, true);
curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
curl_setopt($ch, CURLOPT_HTTPHEADER, [
    'Content-Type: application/json',
    'X-API-Key: optional-api-key'
]);
curl_setopt($ch, CURLOPT_POSTFIELDS, '{}');

$response = curl_exec($ch);
$httpCode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
curl_close($ch);

echo "HTTP $httpCode\n";
echo $response;
```

### Python (Odoo / requests)

```python
import requests

url = "http://127.0.0.1:8765/read"
headers = {
    "X-API-Key": "optional-api-key",
    "Content-Type": "application/json",
}

resp = requests.post(url, json={}, headers=headers, timeout=20)
resp.raise_for_status()
payload = resp.json()

if payload.get("ok"):
    partner_vals = {
        "name": payload["fields"].get("full_name_lat") or payload["fields"].get("full_name_ar"),
        "x_nin": payload["fields"].get("nin"),
        "x_doc_no": payload["fields"].get("doc_no"),
    }
    print("Ready for Odoo create/write:", partner_vals)
else:
    print("Read failed:", payload.get("code"), payload.get("message"))
```
