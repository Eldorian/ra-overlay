using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

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

            // ---- Ping toast test ----
            _app.MapGet("/__ping_toast", async (IHubContext<OverlayHub> hub) =>
            {
                var payload = new { title = "Ping Toast", points = 5, badge = (string?)null };
                await hub.Clients.All.SendAsync("achievement", payload);
                return Results.Ok("sent");
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
            await base.OnConnectedAsync();
        }
    }

    public sealed class OverlayState
    {
        public object? NowPlaying { get; set; }
        public object? Progress { get; set; }
        public List<object> Remaining { get; } = new();
        public object? Next { get; set; }
        public List<object> RecentToasts { get; } = new();
    }

    public sealed record RaOptions
    {
        public string Username { get; init; } = "";  // RA username
        public string WebApiKey { get; init; } = "";  // RA web API key
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
        private int _currentGameId = 0;
        private GameProgress? _lastProgress;
        private DateTime _lastUnlockUtc = DateTime.MinValue;
        private readonly HashSet<int> _seenEarned = new();


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
                _currentGameId = game.GameId;
            }

            _state.NowPlaying = new { title = game.Title, consoleName = game.ConsoleName, boxArt = game.ImageBoxArt };
            await _hub.Clients.All.SendAsync("now-playing", _state.NowPlaying, ct);

            // 2) Progress + remaining from get-game-info-and-user-progress
            var progress = await GetGameProgress(http, _opts.Username, game.GameId, ct);
            _lastProgress = progress;
            var pct = progress.Total > 0 ? (int)Math.Round(progress.Earned / (double)progress.Total * 100) : 0;
            _state.Progress = new { percent = pct, earned = progress.Earned, total = progress.Total };
            await _hub.Clients.All.SendAsync("progress", _state.Progress, ct);

            _state.Remaining.Clear();
            foreach (var a in progress.Remaining)
                _state.Remaining.Add(new { title = a.Title, badge = a.Badge, points = a.Points });
            await _hub.Clients.All.SendAsync("remaining", new { remaining = _state.Remaining }, ct);

            // Fast path: toast anything that just flipped to earned in this poll
            foreach (var ea in progress.EarnedList)
            {
                // only emit once per game session
                if (_seenEarned.Add(ea.Id))
                {
                    // avoid spamming old unlocks on initial connect: require recent within 10 minutes if RA gives us a date
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

            var next = PickNext(_opts.NextSort, progress.Remaining);
            _state.Next = next == null ? null : new { title = next.Title, points = next.Points, badge = next.Badge };
            await _hub.Clients.All.SendAsync("next", _state.Next, ct);

            // 3) New unlocks
            var unlocks = await GetRecentUnlocks(http, _opts.Username, ct);
            foreach (var u in unlocks.OrderBy(x => ParseRaDate(x.Date) ?? DateTime.UtcNow))
            {
                var when = ParseRaDate(u.Date) ?? DateTime.UtcNow;
                if (_announced.Contains(u.Id) && when <= _lastUnlockUtc) continue;

                if (when > _lastUnlockUtc || !_announced.Contains(u.Id))
                {
                    _announced.Add(u.Id);
                    _lastUnlockUtc = when;

                    var payload = new { title = u.Title, points = u.Points, badge = u.BadgeUrl, gameId = u.GameId, date = u.Date };
                    await _hub.Clients.All.SendAsync("achievement", payload, ct);

                    _state.RecentToasts.Add(payload);
                    while (_state.RecentToasts.Count > 5) _state.RecentToasts.RemoveAt(0);

                    // keep progress bar in sync
                    if (_lastProgress is not null && _currentGameId == u.GameId)
                    {
                        _lastProgress.Earned = Math.Min(_lastProgress.Total, _lastProgress.Earned + 1);
                        var pct2 = _lastProgress.Total > 0 ? (int)Math.Round(_lastProgress.Earned / (double)_lastProgress.Total * 100) : 0;
                        _state.Progress = new { percent = pct2, earned = _lastProgress.Earned, total = _lastProgress.Total };
                        await _hub.Clients.All.SendAsync("progress", _state.Progress, ct);
                    }
                }
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
                _ => list.FirstOrDefault(), // "list"
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

            // if the string already starts with '/', it’s a site-relative path
            if (s.StartsWith("/")) return $"https://retroachievements.org{s}";
            // some fields are bare badge names – put them under /Badge/
            if (s.All(char.IsDigit)) return $"https://retroachievements.org/Badge/{s}.png";
            return $"https://retroachievements.org/{s.TrimStart('/')}";
        }

        // v1 docs: get-user-recently-played-games — y (api key), u (username), c (count), o (offset)
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

        // v1 docs: get-game-info-and-user-progress — y, u, g
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
                    var date = GetStr(a, "DateEarnedHardcore", "dateEarnedHardcore", "DateEarned", "dateEarned");
                    var isEarned = !string.IsNullOrWhiteSpace(date);

                    if (isEarned)
                    {
                        earnedList.Add(new EarnedAchievement { Id = id, Title = title, Points = points, Badge = badgeUrl, Date = date });
                    }
                    else
                    {
                        remaining.Add(new RemainingAchievement { Id = id, Title = title, Points = points, Badge = badgeUrl });
                    }
                }
            }

            return new GameProgress
            {
                GameId = gameId,
                Total = total,
                Earned = earned,
                Remaining = remaining,
                EarnedList = earnedList
            };
        }

        // v1 docs: get-user-recent-achievements — y, u, m (minutes lookback)
        private async Task<List<RecentUnlock>> GetRecentUnlocks(HttpClient http, string user, CancellationToken ct)
        {
            var u = Uri.EscapeDataString(user);
            var y = Uri.EscapeDataString(_opts.WebApiKey);
            // Look back 120 minutes so OBS/browser refresh won't miss one
            var url = $"API/API_GetUserRecentAchievements.php?u={u}&y={y}&m=120";
            var json = await http.GetStringAsync(url, ct);

            var arr = JsonSerializer.Deserialize<List<JsonElement>>(json, _json) ?? new();
            var list = new List<RecentUnlock>();
            foreach (var e in arr)
            {
                var badge = GetStr(e, "BadgeURL", "badgeUrl", "BadgeName", "badgeName");
                // if BadgeName (numeric) was supplied, MakeAbsoluteMediaUrl will turn it into /Badge/{id}.png
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

        // ---------- DTOs (internal) ----------
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

        private sealed class EarnedAchievement
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public int Points { get; set; }
            public string? Badge { get; set; }
            public string? Date { get; set; } // RA date string if present
        }

        private sealed class RemainingAchievement
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public string? Badge { get; set; }
            public int Points { get; set; }
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