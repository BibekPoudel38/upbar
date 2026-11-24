# UpBar: Microsoft Store listing

Paste-ready text for Partner Center. Privacy policy: https://github.com/BibekPoudel38/upbar/blob/main/PRIVACY.md

## Properties

- **Category:** Utilities & tools
- **Privacy policy URL:** https://github.com/BibekPoudel38/upbar/blob/main/PRIVACY.md
- **Website:** https://github.com/BibekPoudel38/upbar
- **Support contact:** https://github.com/BibekPoudel38/upbar/issues
- **Pricing:** Free, all markets
- **System requirements:** Windows 10 version 1809 or later, Windows 11 recommended

## Short description (≤ 270 characters)

A fast, GNOME-style top bar for Windows: quick settings, live device batteries, Spotlight-style search, media controls, notes, clipboard history and desktop widgets, all in one lightweight app with springy animations.

## Description

UpBar adds a clean, customizable top bar to Windows, inspired by GNOME and macOS, built to be light (a ~450 KB app) and fast (240 Hz spring animations).

QUICK SETTINGS, ONE CLICK AWAY
Wi-Fi with an inline network picker, Bluetooth with device batteries, sound output and per-app volume, microphone, camera and location privacy, dark mode, power mode, airplane mode, mobile hotspot, keyboard layout, screen cast and a live sound meter that warns you when it gets too loud.

SEARCH EVERYTHING
One box finds apps (including Store apps like Settings and Calculator), jumps to Settings pages, finds your files through the Windows Search index, does maths and unit conversions, and sends anything else to the web or your favourite AI assistant.

HOVER TO PEEK
Hover the media pill for the full now-playing panel, the devices pill for every battery, a workspace dot for the windows on that desktop, or the activity chip for the whole story. Nothing steals your focus.

A SMART ACTIVITY CHIP
Downloads in progress, timers and alarms, meetings about to start with one-click Join, copied links, screen recording and low batteries, surfaced the moment they matter.

MADE FOR YOUR DESK
- Connected headphones, mice, keyboards, phones and controllers with colour-coded battery levels
- Android phone integration: battery, notifications, screen mirroring, file drop and desktop mode
- File shelf, clipboard history with pinned snippets, and a fast Markdown notes app (Alt+N)
- Eight desktop widgets: clock, weather, calendar, system, app grid, devices, now playing and notes
- Nine visual styles, from Acrylic and Glass to Paper, Clay and Brutalist
- Workspace dots, traffic-light window controls, bar tinting, cursor themes and squircle icons
- Hides automatically for full-screen videos and games

PRIVATE BY DESIGN
No accounts, no ads, no telemetry. Everything stays on your PC. UpBar is open source on GitHub.

## Features (one per line, up to 20)

Quick settings for Wi-Fi, Bluetooth, sound, privacy and power
Spotlight-style search for apps, Settings pages and files
Hover tiles for media, devices, desktops and activities
Smart activity chip for downloads, meetings, timers and links
Live battery levels for headphones, mice, keyboards and phones
Per-app volume mixer and live sound meter
Android phone integration with mirroring and desktop mode
File shelf and clipboard history with pinned snippets
Markdown notes with live preview (Alt+N)
Eight snap-to-grid desktop widgets
Nine visual styles, from Acrylic to Brutalist
Workspace dots for Windows virtual desktops
Hides automatically for full-screen apps
Lightweight: about 450 KB, no background polling

## Search terms (up to 7)

top bar, menu bar, status bar, quick settings, gnome, widgets, taskbar

## What's new in this version

First Microsoft Store release: hover tiles for media, devices, desktops and activities; search for Store apps, Settings pages and files; automatic hiding for full-screen apps.

## Notes for certification (Submission options › Notes for certification)

UpBar is a desktop shell enhancement (a top app bar registered with SHAppBarMessage) written in C# for .NET Framework 4.8, packaged as a full-trust MSIX. No account or test credentials are needed: launch it from Start and the bar appears at the top of the primary screen. Right-click the bar or open the tray icon for Settings.

Restricted capabilities:
- runFullTrust: UpBar is a Win32 desktop app (app bar window, WinForms UI).
- unvirtualizedResources: Features the user turns on explicitly change real per-user Windows state that Explorer and Windows must see. These are Settings › System › App icons (writes .ico files and points the user's Start menu/desktop shortcuts at them, with a backup and one-click restore), cursor themes (HKCU\Control Panel\Cursors plus .cur files, restorable), dark mode and the camera/location privacy toggles (HKCU), and "Start with Windows" fallbacks. With write virtualization on, Windows would never see these changes. UpBar's own data lives in %LOCALAPPDATA%\Upbar.
- userNotificationListener: shows the user's notifications in UpBar's notification panel after the user grants Notification access. Nothing is stored or transmitted.

Other notes:
- A low-level keyboard hook (WH_KEYBOARD_LL) watches only the volume up, down and mute keys to show UpBar's own volume overlay. No other keys are read, logged or transmitted.
- Optional Android phone features call adb/scrcpy only if the user has installed them; UpBar doesn't download executables.
- The GitHub build has a self-updater; it's disabled in the Store build (updates come from the Store).
