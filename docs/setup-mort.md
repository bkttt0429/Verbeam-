# MORT Setup

MORT supports a Custom API translation type over HTTP. YomiBridge implements the default MORT request and response shape.

## Steps

1. Start YomiBridge:

```powershell
dotnet run --project .\src\YomiBridge.Api\YomiBridge.Api.csproj
```

2. Open MORT advanced settings.
3. Set translation type to Custom API.
4. Set Custom API URL to:

```text
http://localhost:5757/translate
```

5. Use source and target language codes such as `ja` and `zh-TW`.

## Mobile or Tablet Viewer

For no-overlay play, open the live viewer on a phone, tablet, or second display:

```text
http://localhost:5757/viewer
```

For a phone or tablet on the same LAN, run YomiBridge on a LAN-reachable URL:

```powershell
$env:YB_Urls='http://0.0.0.0:5757'
dotnet run --project .\src\YomiBridge.Api\YomiBridge.Api.csproj
```

Then open this from the device browser:

```text
http://<your-pc-lan-ip>:5757/viewer
```

The viewer receives successful `/translate` results through the `/broadcast` WebSocket endpoint.

## MORT Contract

MORT sends JSON similar to:

```json
{
  "name": "jazh-TW",
  "text": "OCR text",
  "source": "ja",
  "target": "zh-TW"
}
```

YomiBridge returns:

```json
{
  "result": "translated text",
  "errorCode": "0",
  "errorMessage": ""
}
```

On provider failure, YomiBridge returns a nonzero `errorCode` and keeps the original OCR text in `result` so the overlay is not blank.
