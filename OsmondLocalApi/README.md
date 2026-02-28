# OsmondLocalApi

Production-grade .NET 8 x64 Windows Service exposing a local-only HTTP API to read Algerian biometric ID cards and passports using Adaptive Recognition Passport Reader Software (`Pr22.dll`).

## Runtime prerequisites

1. Windows 10/11 x64.
2. Passport Reader Software installed (x64).
3. SDK binaries available in `lib\pr-sdk-2.2`:
   - `Pr22.dll`
   - `Pr22.Processing.dll`
4. Device connected by USB.

## Configuration

Config file is loaded from:

`%ProgramData%\OsmondLocalApi\appsettings.json`

```json
{
  "port": 8765,
  "timeoutSeconds": 10,
  "includePhoto": true,
  "deviceName": "Osmond R V2 SN1234",
  "apiKey": "",
  "authLevel": "Opt"
}
```

- `authLevel`: `Min | Opt | Max`.
- if `apiKey` is set, client must send `X-API-Key`.

## Build and publish

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Published binary location:
`bin\Release\net8.0-windows\win-x64\publish\OsmondLocalApi.exe`

## Install as Windows Service

```bat
sc.exe create OsmondLocalApi binPath= "C:\Services\OsmondLocalApi\OsmondLocalApi.exe" start= auto
sc.exe description OsmondLocalApi "Local Pr22 biometric reader API"
sc.exe start OsmondLocalApi
```

Uninstall:

```bat
sc.exe stop OsmondLocalApi
sc.exe delete OsmondLocalApi
```

## API

### POST `http://127.0.0.1:8765/read`

Returns consistent schema:

- `ok`
- `code` (`OK`, `DEVICE_NOT_FOUND`, `AUTH_FAILED`, ...)
- `fields`
- `raw`
- `images`

If a read is already running, returns HTTP `409` with `READ_IN_PROGRESS`.

## Integration samples

### PHP (cURL)

```php
<?php
$ch = curl_init('http://127.0.0.1:8765/read');
curl_setopt_array($ch, [
    CURLOPT_POST => true,
    CURLOPT_RETURNTRANSFER => true,
    CURLOPT_HTTPHEADER => [
        'Content-Type: application/json',
        'X-API-Key: your-key-if-configured'
    ],
    CURLOPT_TIMEOUT => 20,
]);
$response = curl_exec($ch);
$status = curl_getinfo($ch, CURLINFO_HTTP_CODE);
curl_close($ch);
echo "HTTP $status\n$response\n";
```

### Python (Odoo style)

```python
import requests

url = "http://127.0.0.1:8765/read"
headers = {"X-API-Key": "your-key-if-configured"}
res = requests.post(url, headers=headers, timeout=20)
res.raise_for_status()
data = res.json()
print(data["code"], data["fields"].get("doc_no"))
```

### VB6 / Forms6 pseudo-code

```vb
Dim http As Object
Dim body As String

Set http = CreateObject("MSXML2.ServerXMLHTTP")
http.Open "POST", "http://127.0.0.1:8765/read", False
http.setRequestHeader "Content-Type", "application/json"
http.setRequestHeader "X-API-Key", "your-key-if-configured"
http.send "{}"

If http.Status = 200 Then
    body = http.responseText
    MsgBox body
Else
    MsgBox "Read failed: HTTP " & CStr(http.Status) & vbCrLf & http.responseText
End If
```

## Notes

- The service binds only to loopback (`127.0.0.1`).
- Logs are written to `%ProgramData%\OsmondLocalApi\logs\log-*.txt`.
- Sensitive values (photo bytes/full NIN) are intentionally not logged.
