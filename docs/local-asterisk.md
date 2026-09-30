# Local Asterisk Softphone Loop

This setup lets you test the MoynaPay voice loop without a SIP trunk:
Asterisk runs in Docker, a softphone registers as extension `1001`, and the
voice worker originates a development call to that extension through ARI.

It verifies the local path:

- ARI event stream connects.
- Worker originates a call to a registered softphone.
- The customer hears MoynaPay's generated Bangla prompt.
- DTMF reaches the worker.
- Pressing `1` moves the in-memory test order to `Confirmed`.

It does not verify an IPTSP trunk, carrier audio, NAT on a public server, or a
real merchant database.

## Start Asterisk

```powershell
New-Item -ItemType Directory -Force .local\asterisk\sounds\custom | Out-Null
docker compose up -d asterisk
```

If the default image does not pull on your machine, override it:

```powershell
$env:ASTERISK_IMAGE = "andrius/asterisk:20"
docker compose up -d asterisk
```

## Register A Softphone

Use Zoiper, Linphone, or another SIP softphone:

- SIP server: `127.0.0.1`
- Port: `5060`
- Transport: UDP
- Username / extension: `1001`
- Password: `1001`

Optional sanity check: call extension `600` from the softphone. Asterisk should
answer and play its built-in demo prompt.

## Run The Voice Worker

The worker must write prompt WAV files into the same host directory mounted into
Asterisk:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Telephony__AriBaseUrl = "http://localhost:8088/ari"
$env:Telephony__AriUsername = "moynapay"
$env:Telephony__AriPassword = "moynapay_dev_password"
$env:Telephony__StasisAppName = "moynapay"
$env:Telephony__SoundsPath = "D:\MoynaPayApi\.local\asterisk\sounds\custom"
$env:Telephony__DialEndpointTemplate = "PJSIP/1001"
$env:Telephony__Tts__Model = "<your Gemini TTS model>"
$env:Telephony__Tts__Voice = "<your Gemini voice>"
$env:MoynaPay__Gemini__ApiKey = "<set in your shell, never commit it>"

dotnet run --project src\MoynaPay.Worker.Voice
```

The Gemini key, model, and voice are intentionally not in `appsettings.json`.
Without them the worker starts in Development, but the call will fail when it
tries to synthesize the prompt.

## Place A Development Call

With the softphone registered and the worker running:

```powershell
Invoke-RestMethod -Method Post `
  -Uri http://localhost:5000/dev/calls/softphone `
  -ContentType application/json `
  -Body '{"reference":"LOCAL-1","shopName":"Demo Store","customerName":"Local Caller","amount":1250,"summary":"test order"}'
```

If Kestrel chooses a different port, use the URL printed by `dotnet run`.

Answer the softphone call, listen to the prompt, then press `1`.

Check the order:

```powershell
Invoke-RestMethod http://localhost:5000/dev/orders/LOCAL-1
```

Expected result:

- `status` is `Confirmed`
- `digit` is `1`
- the event list includes `order.confirmed`

## Useful Asterisk Checks

```powershell
docker compose logs -f asterisk
docker compose exec asterisk asterisk -rx "pjsip show contacts"
docker compose exec asterisk asterisk -rx "ari show users"
docker compose exec asterisk asterisk -rx "core show channels"
```

## Notes

Asterisk looks for sounds under language-specific directories before the
language-neutral path. This compose setup mounts the language-neutral custom
directory:

```text
/var/lib/asterisk/sounds/custom
```

The worker writes `.wav` files to `.local/asterisk/sounds/custom` on the host,
and Docker mounts that directory into Asterisk. If you later run the worker in a
container too, keep this as a shared volume rather than writing audio through
ARI.
