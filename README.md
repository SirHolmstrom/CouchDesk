# CouchDesk

<img width="2560" height="1280" alt="image" src="https://github.com/user-attachments/assets/c2ea8eb0-9a0c-44ca-945b-bd7240354bf0" />

CouchDesk puts your Windows PC in a browser tab. Run it on the PC, open a browser, and connect. There is no client to install on the other device, no account to create, and nothing is relayed through somebody else's servers.

## What it does

- Streams your desktop to any modern browser over HTTPS, with LAN-only access by default.
- Works properly on a phone: trackpad-style mouse, keyboard, scrolling, zoom, and draggable controls.
- Supports multiple monitors, adaptive stream quality, and file uploads to the PC.
- Uses low-latency hardware H.264 by default, with automatic JPEG fallback.
- Runs quietly from the Windows tray instead of keeping a window open.
- Uses an owner password plus optional temporary guest codes.

The tray is where most things live. Left-click opens the dashboard. Right-click lets you:

- Open or copy the LAN address.
- Open or close direct remote access.
- Generate **Spectator**, **Control**, or **Full Access** guest codes.
- See and revoke active codes and sessions, with an optional host-only viewer overlay.
- Change stream quality, FPS, and hardware video mode.
- Rename the PC, change the password, manage startup, open logs, or quit.

The tray icon also changes when somebody is actively viewing the PC, which is a small thing I ended up really liking.

## Owner and guest access

The normal **Login** option uses your permanent owner password.

If you want to show a friend something without handing them that password, generate a guest code from the tray:

- **Spectator** can only watch.
- **Control** gets the mouse and ordinary keyboard.
- **Full Access** also gets system keys and file transfer.

Codes expire, can be revoked at any time, and disappear when the app restarts. A guest session never silently turns into an owner session; they have to disconnect and log in again with the owner password.

## Running it

You need Windows 10 or 11 and the .NET 8 SDK.

```bat
run.bat
```

The first run asks for an owner password of at least 12 characters. After that, use the tray icon to open the dashboard.

From the PC itself:

```
https://localhost:8443
```

From another device on the same Wi-Fi:

```
https://<PC-LAN-IP>:8443
```

If Windows Firewall gets in the way, use **Advanced > Add/Repair LAN Firewall Rule** from the tray, or run:

```bat
allowCouchDeskLAN.bat
```

Another device may show a warning for the self-signed certificate the first time it connects.

## Direct remote access

LAN-only is the default.

If you want to connect while away from home, the tray can try UPnP/NAT-PMP or show the values needed for manual port forwarding. It is still a direct connection to your PC. There is no hosted relay or account service in the middle.

That also means your router and ISP get a vote. CGNAT may make incoming connections impossible, and opening a remote-control service to the internet deserves a strong, unique password. The app cannot magically route around either of those facts.

## Phone controls

| Control | What it does |
|---|---|
| One-finger drag | Moves the cursor like a trackpad |
| Hold for about 1.4 seconds | Jumps the cursor to that point |
| Two fingers | Zooms and pans |
| Double-tap | Fits the desktop back to the screen |
| Mouse pad | Left/right click, scroll, and click-drag |
| Keyboard | Types text and shortcuts on the PC |
| Sys keys | Ctrl, Alt, Shift, Win, Del, PrtSc, Esc, and Tab |
| Send file | Saves a file into the PC's Downloads folder |

## A few honest limitations

- Hardware H.264 needs a compatible GPU and browser. CouchDesk falls back to JPEG tiles when it is unavailable.
- UAC prompts and the Windows lock screen appear black because Windows protects that desktop.
- Self-signed certificates are awkward on devices that have not trusted them yet.
- Direct internet access cannot work through every router or ISP setup.

Configuration, certificates, and logs live here:

```
%LOCALAPPDATA%\CouchDesk\
```

Upgrades from RemoteDesktopLAN keep using the old folder automatically so passwords and certificates are not lost.

Uploaded files land here:

```
%USERPROFILE%\Downloads\CouchDesk\
```

## License

[PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0). Use it, change it, share it, all good. Just don't sell it or build a business on it.

| Use | Change | Distribute | Noncommercial |
|---|---|---|---|
| Yes | Yes | Yes | Yes |
