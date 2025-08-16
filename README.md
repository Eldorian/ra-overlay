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

Settings are saved automatically to `%APPDATA%\RaOverlay\settings.json`.  
Your API key is stored **encrypted per Windows user** via DPAPI.

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