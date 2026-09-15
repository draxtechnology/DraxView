# BMX

A self-contained operator display for the Drax Technology service: a logon page and a live events page, with Silence and Reset controls. Built to give a feel for the effort behind an AMX replacement.

One process. Kestrel serves the pages, MQTTnet subscribes to the service's MQTT event mirror, and controls go back on the same broker. Nothing needs the internet; the browser is only the screen.

## Run

Prerequisites: .NET 10 SDK, a Mosquitto broker on localhost:1883, and the Drax Technology service running with `MqttEnabled=true` in its config.

```
dotnet run --project BMX/BMX.csproj
```

Then open http://localhost:5080. First run creates `users.json` with the account `admin` / `admin`; change it from the Settings page.

Broker address, port and topic prefix live in `BMX/appsettings.json`.

## How it fits the service

| Direction | Topic | Payload |
|-----------|-------|---------|
| Service to BMX | `drax/<panel>/event` | JSON from MqttTransfer.PublishEvent (type, on, decoded node/loop/input, text) |
| Service to BMX | `drax/<panel>/log` | Human-readable log line |
| BMX to service | `drax/<panel>/cmd` | The same pipe-command strings the WinForms client sends, e.g. `SILENCE\|0,0,0,0` |

Injecting a test event without a panel: publish `TEST BOX|15,1,0,54` to `drax/<panel>/cmd`.
