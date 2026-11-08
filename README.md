# StrataTray

Windows tray controller for a local [Strata](https://github.com/Niko1221/Strata) LLM server.

- **Start WebUI / Stop WebUI**: the Strata server. Starts lazily (`--lazy`): the server is up in
  seconds with no model loaded, the model loads on first request or **Load Model**.
- **Load Model / Unload Model**: frees or fills VRAM and RAM while the server keeps running.
- **Open Browser**: the Strata web UI (only while the server is up).
- **Settings**: unload the model after N idle minutes (off by default). Writes Strata's own
  `idle_unload_s` into the run config (backup kept); Strata does the idle check and reloads on the
  next request.
- **Model**: every `strata-*.json` in `C:\Strata` is a model, labelled by its `model_name`. Picking
  one switches the run config, restarting the server when it is up, and the choice is remembered in
  `%APPDATA%\StrataTray\settings.json`. A config added later shows up without restarting the tray.
- **Exit**: stops the server, then closes the tray.

Tray badge: green = model loaded, blue moon = server up / model unloaded, amber = loading,
grey = stopped. The tray starts the WebUI on launch if nothing is on the port.

Server output (Strata's own `[strata]` diagnostics) goes to `C:\Strata\server.log`
(previous run kept as `server.log.1`).

## Build

Uses the C# compiler that ships with Windows (.NET Framework 4.x), nothing to install:

```
build.cmd
```

Paths, port and run-config name are constants at the top of `StrataTray.cs`.

## Start at login

```powershell
$s=(New-Object -ComObject WScript.Shell).CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\StrataTray.lnk"); $s.TargetPath='C:\Strata\tray\StrataTray.exe'; $s.WorkingDirectory='C:\Strata\tray'; $s.Save()
```

## Design notes

- All state checks run on a background thread every 5 s; the menu draws from the cached state, so
  it never blocks. Detection is the TCP listener table plus `GET /v1/status` (`loaded`), not WMI.
- WMI is only used on Stop, when the tray did not launch the server itself.

## Control port

The tray listens on `127.0.0.1:8081` so a client can ask for a model by name, which is how a model
picker elsewhere (Goose's, say) can drive the switch:

```
GET  /model                           -> {"model": "...", "state": "...", "models": ["...", ...]}
POST /model {"model": "<model_name>"} -> switches and returns once the server is listening
```

A name that is already serving returns at once. A name no config has is a 409, so a caller finds
out rather than talking to whatever else was loaded. Only the server is restarted: Strata loads
lazily, so the weights load inside the caller's own first request.

Loopback only, and the reply is written straight to the socket, since `HttpListener` would need a
URL reservation or an elevated tray.

Only local programs may use it, never web pages. Any website open in a browser on this PC can send
requests to `127.0.0.1`, so a request is refused (403) when it carries a browser's marks (an `Origin`
or `Sec-Fetch-*` header: cross-site request forgery) or when its `Host` is not `127.0.0.1:8081`,
`localhost:8081` or `[::1]:8081` (DNS rebinding). A POST must be `Content-Type: application/json`,
which a page cannot send cross-site without a CORS preflight, and the port never grants one.

## API key

If Strata has an API key (`STRATA_API_KEY`, else `"api_key"` in the run config), the tray sends it with its own
requests to Strata (status, Load, Unload). Nothing else to set.
