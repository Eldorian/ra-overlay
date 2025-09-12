# RA Overlay (RetroAchievements → OBS)

[![Latest release](https://img.shields.io/github/v/release/Eldorian/ra-overlay?display_name=release&sort=semver)](../../releases/latest)
[![Downloads (latest)](https://img.shields.io/github/downloads/Eldorian/ra-overlay/latest/total)](../../releases/latest)
[![Downloads (total)](https://img.shields.io/github/downloads/Eldorian/ra-overlay/total)](../../releases)

A tiny **.NET 8 (WPF)** desktop app that hosts a local web overlay for OBS and pulls your **RetroAchievements** activity:

- Now playing (game + console + box art)
- Progress bar (earned / total, %)
- “Next up” achievement (configurable sort)
- Remaining achievements (chips)
- Toast when you unlock an achievement (+ optional sound)
- Control Panel: pick any remaining achievement to track, or switch between Auto / Next / Prev
- **Leaderboards (optional)**  
  - Toggle in the app: “Show leaderboard”.  
  - Pick the exact leaderboard you’re grinding from the Control Panel.  
  - Overlay shows Top 5 and your current rank/score.
- **Chips visibility (optional)** — Toggle “Show chips” in the app.

# 🎉 Unlock GIF (New Feature)

You can now display an animated GIF and/or the achievement text in the center of your overlay when an achievement unlocks.

## How to enable
1. Open the desktop app.
2. In **Overlay Options**, check **Show GIF on unlock**.
3. (Optional) Check **Show achievement text under GIF** to display the achievement name below the image.
4. Click **Choose GIF…** to pick a `.gif` file from your computer.  
   The file is copied into the overlay’s `wwwroot` as `unlock.gif`.
5. Set the **GIF duration (ms)** to control how long it stays visible (default: 3000 ms).

## In OBS
No extra source setup needed. The GIF is layered automatically by the overlay, centered on screen, and will play alongside your unlock sound.

## Query string options (advanced)
- `gif=1` – enable GIF display  
- `gifText=1` – show achievement text under GIF  
- `gifMs=3000` – duration in milliseconds  
- `gifUrl=/unlock.gif` – path served by the overlay  

### Example
```
http://localhost:4050/overlay?...&gif=1&gifText=1&gifMs=3000&gifUrl=/unlock.gif
```

**One-click download:**  
👉 [⬇️ Download for Windows (win-x64)](../../releases/latest/download/RaOverlay-win-x64.zip)

> Uses the RA Web API documented here: https://api-docs.retroachievements.org/

---

## ⚡ Quick Start (End Users)

1. **Download** the ZIP above and unzip anywhere.
2. Run `RaOverlay.Desktop.exe`.
3. Enter your **RetroAchievements Username** and **Web API Key**  
   - On RA: Profile → **Settings** → **API** → copy **Web API Key**.
4. (Optional) Click **Choose Sound…** to pick an MP3 that plays when you unlock an achievement.
5. Click **Start**. Copy the **OBS Browser Source URL** shown in the app.
6. In **OBS** → **Sources** → **Browser** → paste the URL and click OK.  
   - Suggested size: Width `800–1200`, Height `200–300`. Background is transparent.
7. (Optional but recommended) Open the **Control Panel** URL (shown above the OBS Url in the app) to pick between which achievement to track or search.

Settings are saved automatically to `%APPDATA%\RaOverlay\settings.json`.  
Your API key is stored **encrypted per Windows user** via DPAPI.

---

## 🎚 Control Panel (OBS Dock / Browser)

The app exposes a built-in control page:
- URL: http://localhost:{PORT}/control (also shown in the app with Copy and Open buttons)
- What it does
  - Shows all remaining achievements (searchable)
  - Click any item to manually select it as “next” (overlay updates instantly, and the large focus card shows title, points, and requirement text)
  - Buttons for Prev, Auto, Next

**Use it as an OBS Dock:**

- OBS → View → Docks → Custom Browser Docks…
  Name: RA Controls, URL: http://localhost:{PORT}/control → Apply

Endpoints (for power users / automation):

| Method	| Path	                    | What it does                          |
|--------:|---------------------------|---------------------------------------|                  
| GET	    | /control	                | Control UI (HTML)                     |
| POST	  | /control/select?id=12345	| Manually select achievement by ID     |
| POST	  | /control/next	            | Cycle to next remaining               |
| POST	  | /control/prev	            | Cycle to previous                     |
| POST	  | /control/auto	            | Return to automatic selection         |

---

## 🎚 Stream Deck Setup

You can control the overlay from an Elgato Stream Deck.

**Option A — HTTP Plugin (recommended)**
1. In the Stream Deck app, open the Store, install an HTTP Request plugin (e.g., BarRaider Stream Deck Tools).
2. Add three buttons (HTTP Request action):
  - Next → Method: POST • URL: http://localhost:{PORT}/control/next
  - Prev → Method: POST • URL: http://localhost:{PORT}/control/prev
  - Auto → Method: POST • URL: http://localhost:{PORT}/control/auto
4. (Optional) Add a Website/Open button for the control page: http://localhost:{PORT}/control

**Option B — No plugin (PowerShell)**

Use a System → Open action with these arguments:
- **Application:** powershell.exe
- **Arguments:**
  - Next: ```-NoLogo -NoProfile -Command "Invoke-RestMethod -Method Post -Uri 'http://localhost:{PORT}/control/next' | Out-Null"```
  - Prev: ```-NoLogo -NoProfile -Command "Invoke-RestMethod -Method Post -Uri 'http://localhost:{PORT}/control/prev' | Out-Null"```
  - Auto: ```-NoLogo -NoProfile -Command "Invoke-RestMethod -Method Post -Uri 'http://localhost:{PORT}/control/auto' | Out-Null"```
  - Ping: ```-NoLogo -NoProfile -Command "Invoke-RestMethod -Uri 'http://localhost:{PORT}/__ping_toast' | Out-Null"```

⚠️ The server binds to localhost. For remote control from another device, you’d need to run RA Overlay on the streaming PC and point the Stream Deck on the same machine at localhost (or modify the server to listen on your LAN IP).

---

## Overlay Options (URL params)

The app generates the URL for you, but for reference:

| Param    | Values / Example         | Notes                          |
|---------:|---------------------------|--------------------------------|
| `pos`    | `tl`, `tr`, `bl`, `br`    | Panel corner                    |
| `alpha`  | `0.75`                    | Panel opacity (0–1)            |
| `blur`   | `8`                       | Backdrop blur (px)             |
| `bg`     | `32,34,38`                | Panel RGB                      |
| `scale`  | `1.0`                     | Overall overlay scale          |
| `width`  | `560`                     | Min panel width (px)           |
| `sound`  | `1` (default) or `0`      | Enable/disable toast sound     |

**Example:**
```http://localhost:4050/overlay?pos=tl&alpha=0.75&blur=8&bg=32,34,38&scale=1.0&width=560```


---

## Troubleshooting

- **OBS shows “Failed to connect to overlay hub”**  
  Make sure the desktop app is **running** and you clicked **Start**. If you changed the port, ensure the URL matches and Windows Firewall allows it.

- **Port already in use**  
  Change the **Port** field in the app and click **Start** again.

- **No sound**  
  Use **Choose Sound…** to pick an MP3. The app copies it to `wwwroot/unlock.mp3`.

- **Settings didn’t stick**  
  Verify the app can write `%APPDATA%\RaOverlay\settings.json`. API key is encrypted per Windows user.

---

## How It Works

- **Desktop app (WPF)** hosts an in-process **ASP.NET Core** server and **SignalR** hub.
- Frontend lives in `wwwroot/index.html` and connects to `/overlayhub`.
- A background worker polls RA endpoints, pushes:
  - **Now Playing** → current game info
  - **Progress** → earned/total and %
  - **Remaining** → unearned achievements
  - **Next up** → first/lowest/highest points (configurable)
  - **Achievement** → toast when a new unlock appears

APIs used (per RA docs):
- `API_GetUserRecentlyPlayedGames`
- `API_GetGameInfoAndUserProgress`
- `API_GetUserRecentAchievements`

---

## Build From Source (Developers)

**Prereqs:** Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download)

```bash
# run
dotnet run --project RaOverlay.Desktop