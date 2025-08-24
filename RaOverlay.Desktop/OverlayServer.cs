using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RaOverlay.Desktop
{
    public sealed class OverlayServer : IAsyncDisposable
    {
        private readonly RaOptions _opts;
        private readonly int _port;
        private WebApplication? _app;

        public string BaseUrl => $"http://localhost:{_port}";

        public OverlayServer(RaOptions opts, int port)
        {
            _opts = opts;
            _port = port;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ApplicationName = typeof(OverlayServer).Assembly.FullName
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });

            builder.Services.AddSignalR();
            builder.Services.AddSingleton(_opts);
            builder.Services.AddSingleton<OverlayState>();
            builder.Services.AddHttpClient("ra", c =>
            {
                c.BaseAddress = new Uri("https://retroachievements.org/");
                c.Timeout = TimeSpan.FromSeconds(15);
            });
            builder.Services.AddHostedService<RaPollingService>();
            builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(_port));

            _app = builder.Build();

            // Static wwwroot
            var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            Directory.CreateDirectory(www);
            _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(www) });
            _app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(www) });

            // SignalR hub
            _app.MapHub<OverlayHub>("/overlayhub");

            // Overlay page
            _app.MapGet("/overlay", async ctx =>
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                var path = Path.Combine(www, "index.html");
                await ctx.Response.SendFileAsync(path, ctx.RequestAborted);
            });

            // Ping toast test
            _app.MapGet("/__ping_toast", async (IHubContext<OverlayHub> hub) =>
            {
                var payload = new { title = "Ping Toast", points = 5, badge = (string?)null };
                await hub.Clients.All.SendAsync("achievement", payload);
                return Results.Ok("sent");
            });

            // -------- Control API --------

            // JSON for the control page
            _app.MapGet("/control/data", (OverlayState s) =>
            {
                var sel = s.GetSelected();
                return Results.Json(new
                {
                    game = s.NowPlaying,
                    manual = s.ManualIndex is not null,
                    selectedId = sel?.Id,
                    remaining = s.RemainingList.Select(a => new
                    {
                        a.Id,
                        a.Title,
                        a.Points,
                        a.Badge,
                        a.Desc
                    })
                });
            });

            // Choose by id
            _app.MapPost("/control/select", async (HttpRequest req, OverlayState s, IHubContext<OverlayHub> hub) =>
            {
                if (!req.Query.TryGetValue("id", out var idVal) || !int.TryParse(idVal, out var id))
                    return Results.BadRequest("id missing");

                var idx = s.RemainingList.FindIndex(a => a.Id == id);
                if (idx < 0) return Results.NotFound("achievement not found");

                s.ManualIndex = idx;
                var a = s.RemainingList[idx];
                await hub.Clients.All.SendAsync("next", new { title = a.Title, points = a.Points, badge = a.Badge, desc = a.Desc });
                return Results.Ok();
            });

            // Manual cycling (kept for convenience)
            _app.MapPost("/control/next", async (OverlayState s, IHubContext<OverlayHub> hub) =>
            {
                if (s.RemainingList.Count == 0) return Results.BadRequest("No remaining");
                s.ManualIndex = ((s.ManualIndex ?? -1) + 1) % s.RemainingList.Count;
                var a = s.RemainingList[s.ManualIndex.Value];
                await hub.Clients.All.SendAsync("next", new { title = a.Title, points = a.Points, badge = a.Badge, desc = a.Desc });
                return Results.Ok();
            });

            _app.MapPost("/control/prev", async (OverlayState s, IHubContext<OverlayHub> hub) =>
            {
                if (s.RemainingList.Count == 0) return Results.BadRequest("No remaining");
                var start = s.ManualIndex ?? 0;
                s.ManualIndex = (start - 1 + s.RemainingList.Count) % s.RemainingList.Count;
                var a = s.RemainingList[s.ManualIndex.Value];
                await hub.Clients.All.SendAsync("next", new { title = a.Title, points = a.Points, badge = a.Badge, desc = a.Desc });
                return Results.Ok();
            });

            _app.MapPost("/control/auto", (OverlayState s) => { s.ManualIndex = null; return Results.Ok(); });

            // Control: list leaderboards for current game + selection
            _app.MapGet("/control/lb-data", (OverlayState s) =>
            {
                var selId = s.SelectedLeaderboardId;
                var cur = s.LeaderboardData is null ? null : new
                {
                    id = s.LeaderboardData.Id,
                    title = s.LeaderboardData.Title,
                    total = s.LeaderboardData.Total,
                    me = s.LeaderboardData.Me
                };
                return Results.Json(new
                {
                    selectedId = selId,
                    current = cur,
                    list = s.Leaderboards.Select(l => new { id = l.Id, title = l.Title, rankAsc = l.RankAsc, format = l.Format })
                });
            });

            // Control: select leaderboard by id
            _app.MapPost("/control/lb-select", async (HttpRequest req, OverlayState s, IHubContext<OverlayHub> hub) =>
            {
                if (!req.Query.TryGetValue("id", out var idVal) || !int.TryParse(idVal, out var id))
                    return Results.BadRequest("id missing");
                if (!s.Leaderboards.Any(l => l.Id == id))
                    return Results.NotFound("leaderboard not found");

                s.SelectedLeaderboardId = id;
                // optimistic push: overlay will show after next poll fetches entries
                await hub.Clients.All.SendAsync("leaderboard", s.LeaderboardData);
                return Results.Ok();
            });


            // Control page (lists all remaining + selectable)
            _app.MapGet("/control", async ctx =>
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync("""
<!doctype html>
<meta charset="utf-8">
<title>RA Overlay Control</title>
<style>
  :root { color-scheme: dark; }
  body{font: 14px system-ui, -apple-system, Segoe UI, Roboto, sans-serif; margin:16px; background:#111; color:#eee}
  h1{font-size:16px;margin:0 0 12px 0}
  .row{display:flex;gap:8px;align-items:center;margin-bottom:10px}
  input[type=search]{padding:8px 10px;border-radius:8px;border:1px solid #333;background:#1b1b1b;color:#fff;min-width:260px}
  button{padding:8px 12px;border:0;border-radius:8px;background:#2d6cdf;color:#fff;cursor:pointer}
  button.secondary{background:#444}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:10px;margin-top:12px}
  .card{background:#1a1a1a;border:1px solid #2a2a2a;border-radius:12px;padding:10px;display:flex;gap:10px;align-items:center}
  .badge{width:48px;height:48px;border-radius:8px;background-size:cover;background-position:center;flex:0 0 auto}
  .title{font-weight:600}
  .pts{opacity:.8}
  .desc{opacity:.9;margin-top:4px;font-size:12px;line-height:1.25}
  .muted{opacity:.7}
  .bar{height:6px;background:#2a2a2a;border-radius:999px;margin-top:4px;overflow:hidden}
  .bar>i{display:block;height:100%;background:#4fd269;width:0%}
  .sel{outline:2px solid #2d6cdf}
</style>
<h1>RA Overlay Control</h1>
<div class="row">
  <input id="q" type="search" placeholder="Search achievements...">
  <button id="prev" class="secondary">Prev</button>
  <button id="auto" class="secondary">Auto</button>
  <button id="next">Next</button>
</div>
<div class="row">
  <select id="lbSel"></select>
  <button id="lbRefresh" class="secondary">Refresh LB</button>
  <div id="lbMe" class="muted"></div>
</div>
<div id="game" class="muted"></div>
<div class="bar"><i id="prog"></i></div>
<div id="grid" class="grid"></div>

<script>
async function getData(){
  const r = await fetch('/control/data'); 
  if(!r.ok) throw new Error('data failed');
  return await r.json();
}
function badgeStyle(url){ return url ? `background-image:url('${url.replace(/'/g,"%27")}')` : '' }

async function getLbData(){ const r = await fetch('/control/lb-data'); return await r.json(); }
async function renderLb(){
  const d = await getLbData();
  const sel = document.getElementById('lbSel');
  sel.innerHTML = '';
  for(const l of d.list){ 
    const o = document.createElement('option');
    o.value = l.id; o.textContent = l.title; 
    if(d.selectedId===l.id) o.selected = true;
    sel.appendChild(o);
  }
  const me = d.current?.me;
  document.getElementById('lbMe').textContent = me ? `You: #${me.rank} • ${me.formattedScore}` : '';
}
document.getElementById('lbSel').onchange = (e)=>fetch('/control/lb-select?id='+e.target.value,{method:'POST'}).then(renderLb);
document.getElementById('lbRefresh').onclick = renderLb;
renderLb(); setInterval(renderLb, 15000);

let items = [], selectedId = null;
function render(data){
  selectedId = data.selectedId;
  document.getElementById('game').textContent =
    data.game?.title ? `${data.game.title} — ${data.manual?'Manual':'Auto'} tracking` : 'Waiting for game...';
  // progress (if present in header text)
  const m = /(\d+)\s*\/\s*(\d+)/.exec('');
  // grid
  const q = document.getElementById('q').value.toLowerCase();
  const grid = document.getElementById('grid');
  grid.innerHTML = '';
  items = data.remaining;
  for(const a of items){
    if(q && !(a.title.toLowerCase().includes(q) || (a.desc||'').toLowerCase().includes(q))) continue;
    const card = document.createElement('div');
    card.className = 'card'+(a.id===selectedId?' sel':'');
    card.innerHTML = `
      <div class="badge" style="${badgeStyle(a.badge)}"></div>
      <div style="min-width:0">
        <div class="title">${a.title}</div>
        <div class="pts">${a.points} pts</div>
        <div class="desc">${a.desc??''}</div>
      </div>`;
    card.onclick = async () => {
      await fetch('/control/select?id='+a.id, {method:'POST'});
      // optimistic UI: mark selected immediately
      document.querySelectorAll('.card.sel').forEach(e=>e.classList.remove('sel'));
      card.classList.add('sel');
    };
    grid.appendChild(card);
  }
}

getData().then(render);
document.getElementById('q').addEventListener('input', () => render({remaining:items, selectedId, game:{}, manual:true}));
document.getElementById('prev').onclick = ()=>fetch('/control/prev',{method:'POST'}).then(()=>getData().then(render));
document.getElementById('auto').onclick = ()=>fetch('/control/auto',{method:'POST'}).then(()=>getData().then(render));
document.getElementById('next').onclick = ()=>fetch('/control/next',{method:'POST'}).then(()=>getData().then(render));
setInterval(()=>getData().then(render).catch(()=>{}), 5000);
</script>
""");
            });

            await _app.StartAsync(ct);
        }

        public Task StopAsync(CancellationToken ct = default) => _app?.StopAsync(ct) ?? Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }

    // Hub returns current state to newly connected clients
    public sealed class OverlayHub : Hub
    {
        private readonly OverlayState _state;
        public OverlayHub(OverlayState state) => _state = state;

        public override async Task OnConnectedAsync()
        {
            if (_state.NowPlaying is not null) await Clients.Caller.SendAsync("now-playing", _state.NowPlaying);
            if (_state.Progress is not null) await Clients.Caller.SendAsync("progress", _state.Progress);
            if (_state.Remaining.Count > 0) await Clients.Caller.SendAsync("remaining", new { remaining = _state.Remaining });
            if (_state.Next is not null) await Clients.Caller.SendAsync("next", _state.Next);
            if (_state.RecentToasts.Count > 0) await Clients.Caller.SendAsync("achievement", _state.RecentToasts[^1]);
            if (_state.LeaderboardData is not null) await Clients.Caller.SendAsync("leaderboard", _state.LeaderboardData);
            await base.OnConnectedAsync();
        }
    }

    public sealed class OverlayState
    {
        public object? NowPlaying { get; set; }
        public object? Progress { get; set; }
        public List<object> Remaining { get; } = new();             // payload for overlay chips

        // Simple list with description for control/selection logic
        public List<SimpleAch> RemainingList { get; } = new();
        public record SimpleAch(int Id, string Title, int Points, string? Badge, string? Desc);

        public object? Next { get; set; }
        public List<object> RecentToasts { get; } = new();

        public int? ManualIndex { get; set; }                        // null => auto
        public SimpleAch? GetSelected()
            => ManualIndex is int idx && idx >= 0 && idx < RemainingList.Count ? RemainingList[idx] : null;
        public List<LbSummary> Leaderboards { get; } = new();
        public int? SelectedLeaderboardId { get; set; }
        public LbData? LeaderboardData { get; set; }

        public record LbSummary(int Id, string Title, string Description, bool RankAsc, string Format, string? TopUser, string? TopScore);
        public record LbEntry(int Rank, string User, string FormattedScore, int Score);
        public record LbData(int Id, string Title, bool RankAsc, string Format, List<LbEntry> Top, LbEntry? Me, int Total);

    }

    public sealed record RaOptions
    {
        public string Username { get; init; } = "";
        public string WebApiKey { get; init; } = "";
        public int PollSeconds { get; init; } = 5;
        public string NextSort { get; init; } = "list"; // list|first|lowest|highest
    }

    public sealed class RaPollingService : BackgroundService
    {
        private readonly ILogger<RaPollingService> _log;
        private readonly IHttpClientFactory _http;
        private readonly OverlayState _state;
        private readonly RaOptions _opts;
        private readonly IHubContext<OverlayHub> _hub;

        private readonly HashSet<int> _announced = new();
        private readonly HashSet<int> _seenEarned = new();
        private int _currentGameId = 0;
        private GameProgress? _lastProgress;
        private DateTime _lastUnlockUtc = DateTime.MinValue;
        private DateTime _lastLbListFetchUtc = DateTime.MinValue;
        private DateTime _lastLbEntriesFetchUtc = DateTime.MinValue;

        public RaPollingService(
            ILogger<RaPollingService> log,
            IHttpClientFactory http,
            OverlayState state,
            RaOptions opts,
            IHubContext<OverlayHub> hub)
        {
            _log = log;
            _http = http;
            _state = state;
            _opts = opts;
            _hub = hub;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_opts.Username) || string.IsNullOrWhiteSpace(_opts.WebApiKey))
            {
                _log.LogWarning("RetroAchievements credentials are missing. Poller idle.");
                return;
            }

            var http = _http.CreateClient("ra");

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await PollOnce(http, stoppingToken); }
                catch (Exception ex) { _log.LogWarning(ex, "Polling error"); }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, _opts.PollSeconds)), stoppingToken);
            }
        }

        private async Task PollOnce(HttpClient http, CancellationToken ct)
        {
            // 1) Current game (most recent)
            var recent = await GetRecentGames(http, ct);
            var game = recent.FirstOrDefault();

            if (game == null)
            {
                _state.NowPlaying = new { title = "Waiting for game...", consoleName = "", boxArt = (string?)null };
                await _hub.Clients.All.SendAsync("now-playing", _state.NowPlaying, ct);
                return;
            }

            if (game.GameId != _currentGameId)
            {
                _announced.Clear();
                _seenEarned.Clear();
                _state.ManualIndex = null;
                _currentGameId = game.GameId;
            }

            _state.NowPlaying = new { title = game.Title, consoleName = game.ConsoleName, boxArt = game.ImageBoxArt };
            await _hub.Clients.All.SendAsync("now-playing", _state.NowPlaying, ct);

            // 2) Progress + remaining (with descriptions)
            var progress = await GetGameProgress(http, _opts.Username, game.GameId, ct);
            _lastProgress = progress;
            var pct = progress.Total > 0 ? (int)Math.Round(progress.Earned / (double)progress.Total * 100) : 0;
            _state.Progress = new { percent = pct, earned = progress.Earned, total = progress.Total };
            await _hub.Clients.All.SendAsync("progress", _state.Progress, ct);

            await FetchLeaderboardsForGame(http, game.GameId, ct);
            await FetchLeaderboardEntries(http, game.GameId, ct);

            // overlay chips + server list
            _state.Remaining.Clear();
            _state.RemainingList.Clear();
            foreach (var a in progress.Remaining)
            {
                _state.Remaining.Add(new { title = a.Title, badge = a.Badge, points = a.Points });
                _state.RemainingList.Add(new OverlayState.SimpleAch(a.Id, a.Title, a.Points, a.Badge, a.Description));
            }
            await _hub.Clients.All.SendAsync("remaining", new { remaining = _state.Remaining }, ct);

            // Selected "Next" (include desc)
            if (_state.RemainingList.Count == 0)
            {
                _state.ManualIndex = null;
                _state.Next = null;
            }
            else if (_state.ManualIndex.HasValue)
            {
                var idx = Math.Clamp(_state.ManualIndex.Value, 0, _state.RemainingList.Count - 1);
                _state.ManualIndex = idx;
                var a = _state.RemainingList[idx];
                _state.Next = new { title = a.Title, points = a.Points, badge = a.Badge, desc = a.Desc };
            }
            else
            {
                var auto = PickNext(_opts.NextSort, progress.Remaining);
                if (auto is null) _state.Next = null;
                else _state.Next = new { title = auto.Title, points = auto.Points, badge = auto.Badge, desc = auto.Description };
            }
            await _hub.Clients.All.SendAsync("next", _state.Next, ct);

            // 3) Fast toasts from progress diff
            foreach (var ea in progress.EarnedList)
            {
                if (_seenEarned.Add(ea.Id))
                {
                    var dt = ParseRaDate(ea.Date) ?? DateTime.UtcNow;
                    if (DateTime.UtcNow - dt <= TimeSpan.FromMinutes(10))
                    {
                        if (_announced.Add(ea.Id))
                        {
                            _lastUnlockUtc = dt;
                            var payload = new { title = ea.Title, points = ea.Points, badge = ea.Badge, gameId = game.GameId, date = ea.Date };
                            await _hub.Clients.All.SendAsync("achievement", payload, ct);
                            _state.RecentToasts.Add(payload);
                            while (_state.RecentToasts.Count > 5) _state.RecentToasts.RemoveAt(0);
                        }
                    }
                }
            }

            // 4) Backup feed (ensures we never miss a toast)
            var unlocks = await GetRecentUnlocks(http, _opts.Username, ct);
            foreach (var u in unlocks.OrderBy(x => ParseRaDate(x.Date) ?? DateTime.UtcNow))
            {
                var when = ParseRaDate(u.Date) ?? DateTime.UtcNow;
                if (_announced.Contains(u.Id) && when <= _lastUnlockUtc) continue;

                _announced.Add(u.Id);
                _lastUnlockUtc = when;

                var payload = new { title = u.Title, points = u.Points, badge = u.BadgeUrl, gameId = u.GameId, date = u.Date };
                await _hub.Clients.All.SendAsync("achievement", payload, ct);

                _state.RecentToasts.Add(payload);
                while (_state.RecentToasts.Count > 5) _state.RecentToasts.RemoveAt(0);
            }
        }

        private static RemainingAchievement? PickNext(string mode, List<RemainingAchievement> list)
        {
            if (list == null || list.Count == 0) return null;
            return (mode?.ToLowerInvariant()) switch
            {
                "first" => list.FirstOrDefault(),
                "lowest" => list.OrderBy(a => a.Points).FirstOrDefault(),
                "highest" => list.OrderByDescending(a => a.Points).FirstOrDefault(),
                _ => list.FirstOrDefault(),
            };
        }

        private static DateTime? ParseRaDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dt))
                return dt;
            return null;
        }

        // +++ helper: fetch leaderboards for current game
        private async Task FetchLeaderboardsForGame(HttpClient http, int gameId, CancellationToken ct)
        {
            if ((DateTime.UtcNow - _lastLbListFetchUtc) < TimeSpan.FromMinutes(2)) return;
            var y = Uri.EscapeDataString(_opts.WebApiKey);
            var url = $"API/API_GetGameLeaderboards.php?i={gameId}&y={y}";
            var json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var list = new List<OverlayState.LbSummary>();
            if (root.TryGetProperty("Results", out var results))
            {
                foreach (var e in results.EnumerateArray())
                {
                    list.Add(new OverlayState.LbSummary(
                        GetInt(e, "ID", "id"),
                        GetStr(e, "Title", "title"),
                        GetStr(e, "Description", "description"),
                        e.TryGetProperty("RankAsc", out var ra) && ra.GetBoolean(),
                        GetStr(e, "Format", "format"),
                        GetStr(e.GetProperty("TopEntry"), "User", "user"),
                        GetStr(e.GetProperty("TopEntry"), "FormattedScore", "formattedScore")
                    ));
                }
            }
            _state.Leaderboards.Clear();
            _state.Leaderboards.AddRange(list);
            _lastLbListFetchUtc = DateTime.UtcNow;
        }

        // +++ helper: fetch entries for selected LB (top 5) and your rank
        private async Task FetchLeaderboardEntries(HttpClient http, int gameId, CancellationToken ct)
        {
            if (_state.SelectedLeaderboardId is null) { _state.LeaderboardData = null; return; }
            if ((DateTime.UtcNow - _lastLbEntriesFetchUtc) < TimeSpan.FromSeconds(30)) return;

            var y = Uri.EscapeDataString(_opts.WebApiKey);
            var lbId = _state.SelectedLeaderboardId.Value;

            // Top entries
            var topJson = await http.GetStringAsync($"API/API_GetLeaderboardEntries.php?i={lbId}&y={y}&c=5", ct);

            using var topDoc = JsonDocument.Parse(topJson);
            var topRoot = topDoc.RootElement;

            var total = GetInt(topRoot, "Total", "total");
            var top = new List<OverlayState.LbEntry>();
            if (topRoot.TryGetProperty("Results", out var arr))
            {
                foreach (var r in arr.EnumerateArray())
                {
                    top.Add(new OverlayState.LbEntry(
                        GetInt(r, "Rank", "rank"),
                        GetStr(r, "User", "user"),
                        GetStr(r, "FormattedScore", "formattedScore"),
                        GetInt(r, "Score", "score")
                    ));
                }
            }

            // Your entry for this game (if any)
            OverlayState.LbEntry? me = null;
            if (!string.IsNullOrWhiteSpace(_opts.Username))
            {
                var u = Uri.EscapeDataString(_opts.Username);
                var mine = await http.GetStringAsync($"API/API_GetUserGameLeaderboards.php?i={gameId}&u={u}&y={y}&c=500", ct);
                using var mineDoc = JsonDocument.Parse(mine);
                var mineRoot = mineDoc.RootElement;
                if (mineRoot.TryGetProperty("Results", out var mineArr))
                {
                    foreach (var r in mineArr.EnumerateArray())
                    {
                        if (GetInt(r, "ID", "id") == lbId && r.TryGetProperty("UserEntry", out var ue))
                        {
                            me = new OverlayState.LbEntry(
                                GetInt(ue, "Rank", "rank"),
                                GetStr(ue, "User", "user"),
                                GetStr(ue, "FormattedScore", "formattedScore"),
                                GetInt(ue, "Score", "score")
                            );
                            break;
                        }
                    }
                }
            }

            var meta = _state.Leaderboards.FirstOrDefault(x => x.Id == lbId);
            _state.LeaderboardData = new OverlayState.LbData(
                lbId,
                meta?.Title ?? $"Leaderboard {lbId}",
                meta?.RankAsc ?? true,
                meta?.Format ?? "",
                top,
                me,
                total
            );

            await _hub.Clients.All.SendAsync("leaderboard", _state.LeaderboardData, ct);
            _lastLbEntriesFetchUtc = DateTime.UtcNow;
        }


        // ---------- RA API helpers ----------

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private static string MakeAbsoluteMediaUrl(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p ?? "";
            var s = p.Trim();
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return s;

            if (s.StartsWith("/")) return $"https://retroachievements.org{s}";
            if (s.All(char.IsDigit)) return $"https://retroachievements.org/Badge/{s}.png";
            return $"https://retroachievements.org/{s.TrimStart('/')}";
        }

        private async Task<List<RecentGame>> GetRecentGames(HttpClient http, CancellationToken ct)
        {
            var u = Uri.EscapeDataString(_opts.Username);
            var y = Uri.EscapeDataString(_opts.WebApiKey);
            var url = $"API/API_GetUserRecentlyPlayedGames.php?u={u}&y={y}&c=1";
            var json = await http.GetStringAsync(url, ct);

            var arr = JsonSerializer.Deserialize<List<JsonElement>>(json, _json) ?? new();
            var list = new List<RecentGame>();
            foreach (var e in arr)
            {
                list.Add(new RecentGame
                {
                    GameId = GetInt(e, "GameID", "gameId"),
                    Title = GetStr(e, "Title", "title"),
                    ConsoleName = GetStr(e, "ConsoleName", "consoleName"),
                    ImageBoxArt = MakeAbsoluteMediaUrl(GetStr(e, "ImageBoxArt", "imageBoxArt"))
                });
            }
            return list;
        }

        private async Task<GameProgress> GetGameProgress(HttpClient http, string user, int gameId, CancellationToken ct)
        {
            var u = Uri.EscapeDataString(user);
            var y = Uri.EscapeDataString(_opts.WebApiKey);
            var url = $"API/API_GetGameInfoAndUserProgress.php?g={gameId}&u={u}&y={y}";
            var json = await http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var total = GetInt(root, "NumAchievements", "numAchievements");
            var earned = GetInt(root, "NumAwardedToUser", "numAwardedToUser");

            var remaining = new List<RemainingAchievement>();
            var earnedList = new List<EarnedAchievement>();

            if (root.TryGetProperty("Achievements", out var achs) || root.TryGetProperty("achievements", out achs))
            {
                foreach (var kv in achs.EnumerateObject())
                {
                    var a = kv.Value;
                    var id = GetInt(a, "ID", "id");
                    var title = GetStr(a, "Title", "title");
                    var points = GetInt(a, "Points", "points");
                    var badgeNameOrUrl = GetStr(a, "BadgeName", "badgeName", "BadgeURL", "badgeUrl");
                    var badgeUrl = MakeAbsoluteMediaUrl(badgeNameOrUrl);
                    var desc = GetStr(a, "Description", "description");
                    var date = GetStr(a, "DateEarnedHardcore", "dateEarnedHardcore", "DateEarned", "dateEarned");
                    var isEarned = !string.IsNullOrWhiteSpace(date);

                    if (isEarned)
                        earnedList.Add(new EarnedAchievement { Id = id, Title = title, Points = points, Badge = badgeUrl, Date = date });
                    else
                        remaining.Add(new RemainingAchievement { Id = id, Title = title, Points = points, Badge = badgeUrl, Description = desc });
                }
            }

            return new GameProgress
            {
                GameId = gameId,
                Earned = earned,
                Total = total,
                Remaining = remaining,
                EarnedList = earnedList
            };
        }

        private async Task<List<RecentUnlock>> GetRecentUnlocks(HttpClient http, string user, CancellationToken ct)
        {
            var u = Uri.EscapeDataString(user);
            var y = Uri.EscapeDataString(_opts.WebApiKey);
            var url = $"API/API_GetUserRecentAchievements.php?u={u}&y={y}&m=120";
            var json = await http.GetStringAsync(url, ct);

            var arr = JsonSerializer.Deserialize<List<JsonElement>>(json, _json) ?? new();
            var list = new List<RecentUnlock>();
            foreach (var e in arr)
            {
                var badge = GetStr(e, "BadgeURL", "badgeUrl", "BadgeName", "badgeName");
                var badgeUrl = MakeAbsoluteMediaUrl(badge);

                list.Add(new RecentUnlock
                {
                    Id = GetInt(e, "AchievementID", "achievementId"),
                    Title = GetStr(e, "Title", "title"),
                    Points = GetInt(e, "Points", "points"),
                    GameId = GetInt(e, "GameID", "gameId"),
                    BadgeUrl = badgeUrl,
                    Date = GetStr(e, "Date", "date")
                });
            }
            return list;
        }

        // ---------- JSON helpers ----------
        private static string GetStr(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null)
                    return v.ToString();
            return "";
        }
        private static int GetInt(JsonElement e, params string[] names)
        {
            foreach (var n in names)
            {
                if (e.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                    if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
                }
            }
            return 0;
        }

        // ---------- DTOs ----------
        private sealed class RecentGame
        {
            public int GameId { get; set; }
            public string Title { get; set; } = "";
            public string ConsoleName { get; set; } = "";
            public string? ImageBoxArt { get; set; }
        }

        private sealed class GameProgress
        {
            public int GameId { get; set; }
            public int Earned { get; set; }
            public int Total { get; set; }
            public List<RemainingAchievement> Remaining { get; set; } = new();
            public List<EarnedAchievement> EarnedList { get; set; } = new();
        }

        private sealed class RemainingAchievement
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public string? Badge { get; set; }
            public int Points { get; set; }
            public string? Description { get; set; }
        }

        private sealed class EarnedAchievement
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public int Points { get; set; }
            public string? Badge { get; set; }
            public string? Date { get; set; }
        }

        private sealed class RecentUnlock
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public int Points { get; set; }
            public int GameId { get; set; }
            public string? BadgeUrl { get; set; }
            public string? Date { get; set; }
        }
    }
}