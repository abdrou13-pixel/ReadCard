# OsmondLocalApi

Windows Service (.NET 8, x64) exposing local-only HTTP API for Algerian biometric ID/passport reading through Adaptive Recognition `pr-sdk-2.2` and Osmond R V2.

## Runtime requirements

- Windows 10/11 x64
- Passport Reader Software installed
- `pr-sdk-2.2` .NET assemblies available
- Reader connected via USB

## Project structure

- `Program.cs`: Minimal API + Windows Service hosting + ProgramData config/logging
- `Services/OsmondReaderService.cs`: PR22 lifecycle orchestration (scan/analyze/auth/read/extract)
- `Models/ReadResponse.cs`: required output schema

## Configuration

File path:

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

## Logging

Daily rolling logs written to:

`%ProgramData%\OsmondLocalApi\logs`

## Endpoint

### POST `http://127.0.0.1:8765/read`

- Optional header: `X-API-Key` (required when configured)
- If read already in progress: HTTP 409 + `READ_IN_PROGRESS`

### Response

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

## PR22 integration behavior implemented

- Device opened on startup and reused.
- Read pipeline:
  1) Scan White + Infra
  2) Analyze MRZ + VIZ
  3) Start `ECardTask` with `AuthLevel.Full` and `FileId.All`
  4) Handle `AuthBegin`, `AuthWaitForInput`, `AuthFinished`
  5) Await `ReadFinished(FileId.All)` via `TaskCompletionSource`
  6) Extract output fields and raw values
- Chip auth failure returns `READ_FAILED`.
- Photo selection priority: DG2 face (`FieldSource.ECard`) then fallback VIZ face.

## SDK binary placement

Put these files under:

`lib\pr-sdk-2.2\`

- `Pr22.dll`
- `Pr22.Processing.dll`

## Publish self-contained x64

```powershell
dotnet restore
dotnet publish .\OsmondLocalApi.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\publish
```

## Register Windows Service (sc.exe)

```powershell
sc.exe create OsmondLocalApi binPath= "C:\Program Files\OsmondLocalApi\OsmondLocalApi.exe" start= auto
sc.exe description OsmondLocalApi "Local API for Algerian eID/passport read via Osmond R V2"
sc.exe start OsmondLocalApi
```

## PHP cURL example

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
$status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
curl_close($ch);

echo "HTTP $status\n";
echo $response;
```

## Python (Odoo requests) example

```python
import requests

resp = requests.post(
    "http://127.0.0.1:8765/read",
    json={},
    headers={"X-API-Key": "optional-api-key"},
    timeout=20,
)
print(resp.status_code, resp.json())
```
