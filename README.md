# RA Overlay (RetroAchievements → OBS)

A tiny **.NET 8** desktop app that hosts a local web overlay for OBS.  
It pulls your **RetroAchievements** activity and shows:

- Now playing (game + box art + console)
- Progress bar (earned/total, %)
- “Next up” achievement (configurable sort)
- Remaining achievements (chips)
- Toast when you unlock an achievement (+ optional sound)

https://api-docs.retroachievements.org/

---

## Quick Start

1. **Download** the latest release ZIP from the Releases page and unzip anywhere.
2. Run `RaOverlay.Desktop.exe`.
3. Enter your **RetroAchievements Username** and **Web API Key**  
   - Web API Key: RA → your profile → **Settings** → **API**.
4. (Optional) Click **Choose Sound…** to pick an MP3 that plays on unlock.
5. Click **Start**. Copy the **OBS Browser Source URL**.
6. In **OBS** → **Sources** → **Browser** → paste the URL.  
   Recommended: Width `800–1200`, Height `200–300`. Background is transparent.

**Tip:** Drag the overlay around by changing `pos`, `alpha`, `blur`, `scale`, etc. in the app—the URL updates instantly.

Settings are persisted to `%APPDATA%\RaOverlay\settings.json`.  
Your API key is stored **encrypted per Windows user** (DPAPI).

---

## Overlay Options (URL params)

The app generates the URL for you, but for reference:

- `pos` — `tl`, `tr`, `bl`, `br` (top/bottom, left/right)
- `alpha` — panel opacity (e.g. `0.75`)
- `blur` — background blur in px (e.g. `8`)
- `bg` — RGB triple for panel (e.g. `32,34,38`)
- `scale` — overall overlay scale (e.g. `1.00`)
- `width` — min panel width in px (e.g. `560`)
- `sound` — `1` (default) or `0` to mute toasts

Example:
```
http://localhost:4050/overlay?pos=tl&alpha=0.75&blur=8&bg=32,34,38&scale=1.0&width=560
```

---

## Troubleshooting

- **Nothing shows in OBS / “Failed to connect to overlay hub”:**  
  Make sure the desktop app is **running** and you clicked **Start**. Check Windows Firewall if you changed the port.
- **Port already in use:**  
  Change the Port field in the app and Start again.
- **No sound:**  
  Pick an MP3 via **Choose Sound…**. The app copies it to `wwwroot/unlock.mp3`.
- **Settings lost:**  
  Ensure the app can write to `%APPDATA%\RaOverlay`. The file is `settings.json`.

---

## Dev Setup

- **Prereqs:** Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download).
- **Run:**
  ```bash
  dotnet run --project RaOverlay.Desktop
