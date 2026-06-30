# Remote Desktop LAN
Your Windows PC, in a browser tab, driven from the couch. Or the kitchen. Or your phone in bed at 2am because you left something running. No client to install, no account to make, nothing bouncing through some company's servers in another country. It lives on your network and never leaves.
> **Lightweight** and I found myself constantly needing a remote access solution for my PC that don't require a bunch of bloated softwares -> one .bat to setup and either one .bat to open port's or use Powershell and we are ready!

Written from scratch in C# / .NET 8 with a frontend of plain HTML and JavaScript. No build step, no framework, no 400MB of node_modules. The screen streams over an encrypted WebSocket, and your phone turns into a trackpad, a keyboard, and a little pile of shortcut buttons.

> **One rule: keep it on the LAN.** This is a home-and-office-network tool. I did not build it to survive the open internet, so please don't port-forward it or drop it on some sketchy network. You've been warned, and so has your PC.

<img width="2560" height="1280" alt="hero" src="https://github.com/user-attachments/assets/34209bca-1e5c-49a3-bc3d-166959aa4776" />

## Features

- **Runs in any browser.** If it can render this century's HTML, it can be your remote. Nothing to install on the controlling side.
- **Properly usable from a phone.** A trackpad-style cursor, a floating mouse pad, a real on-screen keyboard, and a couple of shortcut panels. You're not jabbing at 12px close buttons with your thumb.
- **Locks itself down.** Argon2id on the password, DPAPI on the stored secrets, self-signed TLS that installs itself as trusted so the browser stops whining, sessions that expire, per-IP lockout with backoff for anyone trying to guess their way in, and an audit log that remembers.
- **Multi-monitor.** Flip between screens without reconnecting.
- **Tune it live.** Quality, frame rate, and which monitor, all changeable mid-session.
- **Only sends what moved.** The screen is chopped into 128px tiles, each one hashed. If a tile didn't change, it doesn't get sent, so a still screen costs almost nothing. Every 7 seconds a full keyframe rolls through to sweep up anything stale.
- **Every input you need.** Absolute mouse across the whole virtual desktop, scroll wheel, all three buttons, Unicode typing, modifier combos, and yes, a Ctrl+Alt+Del button.
- **Throw a file at your PC.** Send a screenshot (or anything else) from the phone straight to the PC's Downloads.
- **An off switch.** Kill remote access without killing the app.

## What you'll need

- Windows 10 or 11. It targets `net8.0-windows` and lives on GDI, SendInput, DPAPI, and UI Automation.
- The .NET 8 SDK to build it.
- A browser from this decade on whatever you're controlling from.

## Getting it running

Build and go:

```bat
run.bat
```

Builds the project and starts the server on port 8443 over HTTPS. On first launch it mints a self-signed certificate and tucks it into your user's Trusted Root store so `wss://` connects without a fuss.

On the same machine:

```
https://localhost:8443
```

To reach it from your phone, to crack the port open to your local subnet once, or from an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "RemoteDesktopLAN" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8443 -RemoteAddress LocalSubnet
```
or run:
```
allowRemoteDesktopLAN.bat
```

Find the PC's LAN IP (`ipconfig`, the IPv4 line) and on the phone, same Wi-Fi, head to:

```
https://<PC-LAN-IP>:8443
```

First visit on each device throws a certificate warning. Tell it you trust yourself, and you're in.

Want it to wake up when you log in?

```bat
install-autostart.bat
uninstall-autostart.bat
```

## First run

It'll ask you to set an admin password, 12 characters minimum. That password only ever exists as an Argon2id hash; the plaintext never touches the disk. From then on, that's your key in.

## Driving it

On a desktop it's exactly what your hands expect: move, click, scroll, type. The gear up in the top-left holds the controls: monitor, quality, FPS, full screen, Ctrl+Alt+Del, disconnect.

On a phone:

| Gesture or control | What happens |
|---|---|
| One-finger drag | Cursor moves like a trackpad. Lift and re-plant your finger to keep going. |
| Hold still about 1.4s | Cursor teleports to that spot |
| Two fingers | Pinch to zoom, drag to pan |
| Double-tap | Snaps the screen back to fit |
| Mouse pad (✋) | Left and right click, a scroll strip, and a Hold latch for click-and-drag |
| Keyboard (⌨) | On-screen keyboard with Shift, symbols, modifier combos, key repeat, and a strip that echoes what you've typed |
| Sys keys (⌃) | Ctrl, Alt, Shift, Win that latch on, plus Del, PrtSc, Esc, Tab. Latch Shift to multi-select files, or latch Win and tap the arrows to snap windows. |
| Arrows (⇅) | Arrow keys with hold-to-repeat |
| Send file (📤) | Flings a file from the phone to the PC's Downloads |

Every floating panel drags wherever you like, and they scramble back on-screen if you rotate the phone instead of vanishing off the edge.

## Screenshots

<img width="611" height="263" alt="Settings" src="https://github.com/user-attachments/assets/7aa876c3-9d09-4f3f-a61e-c985ed2bed12" />
<img width="611" height="263" alt="Keyboard" src="https://github.com/user-attachments/assets/87b1d87b-5387-4756-affd-407456db5e08" />


## Where it keeps its stuff

The config is a JSON file in the app's data folder: port, bind address, session timeout, and the remote-access on/off flag. The same folder holds the certificate, the password hash, and the audit log:

```
%LOCALAPPDATA%\RemoteDesktopLAN\
```

Anything you send from a phone lands here:

```
%USERPROFILE%\Downloads\RemoteDesktopLAN\
```

## Under the hood

```
  browser (viewer)
       |  ^
   WSS |  | JSON control
       v  |
  Kestrel / ASP.NET Core
       |
       +--> GDI screen capture (tile-delta)  ...frames fly back to the browser
       |
       +--> Win32 SendInput (mouse + keyboard)
```

`GdiScreenCapturer` grabs the screen the old-fashioned way with GDI, slices it into 128px tiles, hashes each with FNV-1a, and ships only the tiles that changed since last time, as JPEG. Every 7 seconds it forces a keyframe so a dropped tile can't sit there looking wrong forever. The cursor gets painted into the frame too, because a remote desktop where you can't see the pointer is just a screenshot.

Frames travel over the WebSocket as raw binary; everything chatty (input, settings, ping) is JSON. HTTP/2 is switched off on purpose. Leave it on and the WebSocket upgrade quietly refuses to happen, which cost me an evening once.

Input runs through `InputInjector`, a wrapper around `SendInput`. The mouse is mapped across the entire virtual desktop so your second and third monitors land in the right place, text goes in as Unicode, and the special keys and combos use virtual-key codes. A few keys (the Win key, the arrows) need the extended-key flag or Windows just shrugs and ignores them, so they get it.

The whole capture side hides behind an `IScreenCapturer` interface. The day I get tired of GDI eating CPU and wire up something GPU-accelerated (Windows.Graphics.Capture plus H.264), nothing else in the app has to know.

Worth knowing up front: it runs in your normal user session, so it can't see or touch the UAC secure desktop (the dimmed "are you sure" prompt) or the lock screen. Those come through as a black rectangle. That's Windows drawing a hard line, and I'm not going to pretend I can erase it.


## Rough edges

- **The capture eats CPU.** GDI plus JPEG is dead simple and drags in zero dependencies, but it isn't touching the GPU, so crank the resolution and FPS and your CPU will feel it. WGC plus H.264 is the eventual fix.
- **The secure desktop stays secret.** UAC prompts and the lock screen come through pure black. Nothing I can do, that's the OS guarding the door.
- **Self-signed cert.** One browser warning per device, then it leaves you alone.
- **One user, one password.** No accounts, no roles, no sharing.
- **Not internet-ready.** No reverse proxy, no fancy rate limiting, none of the armor you'd want facing the world. LAN. Only.
- **Landscape on a small phone gets busy** if you fan out every panel at once.

## Someday, maybe..!

- Clipboard sync both directions, with a little history
- Files going the other way, PC to phone
- A tray app to toggle access and see (or boot) whoever's connected
- GPU-accelerated capture (Windows.Graphics.Capture + H.264 / WebCodecs)

## License

PolyForm Noncommercial 1.0.0. Use it, change it, share it, all good. Just don't sell it or build a business on it.

| Use | Change | Distribute | Noncommercial |
|---|---|---|---|
| Yes | Yes | Yes | Yes |

Full text: https://polyformproject.org/licenses/noncommercial/1.0.0
