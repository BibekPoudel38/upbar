# UpBar Privacy Policy

_Last updated: November 23, 2026_

UpBar is a top bar, quick settings panel and widget suite for Windows. It runs entirely on your PC. **UpBar has no accounts, no analytics, no telemetry and no ads, and it never sells or shares your data.**

## What stays on your PC

UpBar reads the following only to show it to you, and keeps it on your device:

- **Notifications:** with your permission (Windows *Notification access*), UpBar shows the titles and text of your recent notifications in its notification panel and activity chip. They are never stored by UpBar or sent anywhere.
- **Microphone level:** the optional sound meter reads the loudness level (in dB) to warn you about loud environments. It doesn't record, save or send any audio.
- **Bluetooth, Wi-Fi and devices:** UpBar lists nearby Wi-Fi networks, connected Bluetooth devices and their battery levels so you can see and switch them. Wi-Fi passwords you type are passed straight to Windows to connect.
- **Clipboard, notes and files:** clipboard history, pinned snippets, Markdown notes, the file shelf and app grid are saved as plain files in UpBar's own folder (`%LOCALAPPDATA%\Upbar` for the Microsoft Store version, or next to `Upbar.exe` for the GitHub version). You can delete them at any time.
- **Search:** app, Settings and file results come from your own Start menu and the Windows Search index on your PC.
- **Phone (optional):** if you connect an Android phone with USB/wireless debugging, UpBar talks to it locally through `adb`/`scrcpy` on your own machine.

## What goes over the network

UpBar itself connects only to these services, and only when you use the feature:

| Service | When | What is sent |
| :--- | :--- | :--- |
| [Open-Meteo](https://open-meteo.com/) | The Weather widget refreshes | The city name you entered, to look up its forecast. No personal data or precise location. |
| [GitHub](https://github.com/BibekPoudel38/upbar) | You click *Check for updates* (GitHub version only; the Microsoft Store version is updated by the Store) | A request for the latest release version. |

When you search the web or ask an AI service (Google, Bing, DuckDuckGo, ChatGPT, Claude, Gemini, Perplexity or a custom engine) from UpBar's search box, UpBar opens that site **in your default browser** with your query. That site's own privacy policy then applies.

## Changes to Windows settings

Some features change Windows settings at your request and can be undone in UpBar: dark mode, camera and location access switches, Wi-Fi/Bluetooth/airplane mode, cursor themes, and squircle icons for your shortcuts (originals are backed up and restored when you switch back).

## Children

UpBar doesn't knowingly collect any information from anyone, including children.

## Contact

Questions or concerns: open an issue at <https://github.com/BibekPoudel38/upbar/issues>.
