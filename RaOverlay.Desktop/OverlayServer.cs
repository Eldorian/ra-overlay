using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RaOverlay.Desktop;

public sealed class OverlayServer : IAsyncDisposable
{
    private readonly RaOptions _opts;
    private readonly int _port;
    private WebApplication? _app;

    public OverlayServer(RaOptions opts, int port) { _opts = opts; _port = port; }
    public string BaseUrl => $"http://localhost:{_port}";

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls(BaseUrl);

        builder.Services.AddSignalR();
        builder.Services.AddSingleton(new OverlayState());
        builder.Services.AddHttpClient("RA", http =>
        {
            http.BaseAddress = new Uri("https://retroachievements.org");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RaOverlay/desktop");
        });
        builder.Services.AddSingleton(_opts);
        builder.Services.AddHostedService<RaPollingService>();

        _app = builder.Build();

        var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(www) });
        _app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(www) });

        _app.MapHub<OverlayHub>("/overlayhub");
        _app.MapGet("/overlay", async ctx => await ctx.Response.SendFileAsync(Path.Combine(www, "index.html")));

        await _app.StartAsync(ct);
    }

    public Task StopAsync(CancellationToken ct = default)
        => _app is null ? Task.CompletedTask : _app.StopAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

public class OverlayHub : Hub
{
    private readonly OverlayState _state;
    public OverlayHub(OverlayState state) => _state = state;

    public override async Task OnConnectedAsync()
    {
        if (_state.NowPlaying is not null) await Clients.Caller.SendAsync("now-playing", _state.NowPlaying);
        if (_state.Progress   is not null) await Clients.Caller.SendAsync("progress", _state.Progress);
        if (_state.Remaining.Count > 0)    await Clients.Caller.SendAsync("remaining", new { remaining = _state.Remaining });
        if (_state.Next       is not null) await Clients.Caller.SendAsync("next", _state.Next);
        await base.OnConnectedAsync();
    }
}

public record RaOptions
{
    public string Username { get; init; } = string.Empty;
    public string WebApiKey { get; init; } = string.Empty;
    public int PollSeconds { get; init; } = 5;
    public string NextSort { get; init; } = "list"; // list | points-asc | points-desc
}

public class OverlayState
{
    public object? NowPlaying { get; set; }
    public object? Progress { get; set; }
    public List<object> Remaining { get; set; } = new();
    public object? Next { get; set; }
}

public sealed class RaPollingService : BackgroundService
{
    private readonly ILogger<RaPollingService> _log;
    private readonly IHttpClientFactory _http;
    private readonly IHubContext<OverlayHub> _hub;
    private readonly RaOptions _opts;
    private readonly OverlayState _state;
    private readonly HashSet<int> _announced = new();
    private int? _currentGameId;
    private GameProgress? _lastProgress;

    public RaPollingService(ILogger<RaPollingService> log, IHttpClientFactory http, IHubContext<OverlayHub> hub, RaOptions opts, OverlayState state)
    { _log = log; _http = http; _hub = hub; _opts = opts; _state = state; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(3, _opts.PollSeconds)));
        do { try { await Poll(stoppingToken); } catch (Exception ex) { _log.LogWarning(ex, "Polling error"); } }
        while (await tick.WaitForNextTickAsync(stoppingToken));
    }

    private async Task Poll(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.Username) || string.IsNullOrWhiteSpace(_opts.WebApiKey)) return;
        var ra = _http.CreateClient("RA");

        var recent = await GetUserRecentlyPlayedGameAsync(ra, _opts.Username, _opts.WebApiKey, ct);
        if (recent?.GameId is int gid && _currentGameId != gid)
        {
            _currentGameId = gid;
            _lastProgress = await GetGameInfoAndUserProgressAsync(ra, gid, _opts.Username, _opts.WebApiKey, ct);
            if (_lastProgress is not null)
            {
                var nowPlayingPayload = new
                {
                    gameId = _lastProgress.GameId,
                    title = _lastProgress.Title,
                    consoleName = _lastProgress.ConsoleName,
                    boxArt = _lastProgress.BoxArt,
                    percent = _lastProgress.Percent,
                    earned = _lastProgress.Earned,
                    total = _lastProgress.Total
                };
                _state.NowPlaying = nowPlayingPayload;
                _state.Progress = new { percent = _lastProgress.Percent, earned = _lastProgress.Earned, total = _lastProgress.Total };
                _state.Remaining = _lastProgress.Remaining.ConvertAll(a => (object)new { a.Id, a.Title, a.Badge, a.Points });
                await _hub.Clients.All.SendAsync("now-playing", nowPlayingPayload, ct);
                await _hub.Clients.All.SendAsync("remaining", new { remaining = _lastProgress.Remaining.GetRange(0, Math.Min(8, _lastProgress.Remaining.Count)).ToArray() }, ct);
            }
        }

        if (_currentGameId is int gameId)
        {
            _lastProgress = await GetGameInfoAndUserProgressAsync(ra, gameId, _opts.Username, _opts.WebApiKey, ct);
            if (_lastProgress is not null)
            {
                var prog = new { percent = _lastProgress.Percent, earned = _lastProgress.Earned, total = _lastProgress.Total };
                _state.Progress = prog;
                _state.Remaining = _lastProgress.Remaining.ConvertAll(a => (object)new { a.Id, a.Title, a.Badge, a.Points });
                await _hub.Clients.All.SendAsync("progress", prog, ct);
                await _hub.Clients.All.SendAsync("remaining", new { remaining = _lastProgress.Remaining.GetRange(0, Math.Min(8, _lastProgress.Remaining.Count)).ToArray() }, ct);
                var next = PickNext(_lastProgress.Remaining);
                _state.Next = next is null ? null : new { id = next.Id, title = next.Title, badge = next.Badge, points = next.Points };
                await _hub.Clients.All.SendAsync("next", _state.Next, ct);
            }
        }

        var newUnlocks = await GetUserRecentAchievementsAsync(ra, _opts.Username, _opts.WebApiKey, minutes: 5, ct);
        foreach (var u in newUnlocks)
        {
            if (_announced.Add(u.Id))
            {
                await _hub.Clients.All.SendAsync("achievement", new
                {
                    id = u.Id, title = u.Title, description = u.Description, points = u.Points,
                    gameId = u.GameId, gameTitle = u.GameTitle, hardcore = u.Hardcore, badge = u.BadgeUrl, date = u.Date
                }, ct);
                if (_lastProgress is not null && _currentGameId == u.GameId)
                {
                    _lastProgress.Earned = Math.Min(_lastProgress.Total, _lastProgress.Earned + 1);
                    _lastProgress.Percent = _lastProgress.Total > 0 ? (int)Math.Round(_lastProgress.Earned / (double)_lastProgress.Total * 100) : 0;
                    _lastProgress.Remaining.RemoveAll(r => r.Id == u.Id);
                    var prog2 = new { percent = _lastProgress.Percent, earned = _lastProgress.Earned, total = _lastProgress.Total };
                    _state.Progress = prog2; await _hub.Clients.All.SendAsync("progress", prog2, ct);
                }
            }
        }
    }

    private RemainingAchievement? PickNext(IEnumerable<RemainingAchievement> remaining)
        => _opts.NextSort switch
        {
            "points-asc" => Enumerable.OrderBy(remaining, r => r.Points).ThenBy(r => r.Title).FirstOrDefault(),
            "points-desc" => Enumerable.OrderByDescending(remaining, r => r.Points).ThenBy(r => r.Title).FirstOrDefault(),
            _ => Enumerable.FirstOrDefault(remaining),
        };

    // === RA HTTP helpers ===
    private static string WithKey(string path, string key)
        => $"{path}{(path.Contains('?') ? "&" : "?")}y={WebUtility.UrlEncode(key)}";
    private static readonly Uri RaMedia = new("https://retroachievements.org");

    private static async Task<RecentGame?> GetUserRecentlyPlayedGameAsync(HttpClient http, string username, string key, CancellationToken ct)
    {
        var url = WithKey($"/API/API_GetUserRecentlyPlayedGames.php?u={WebUtility.UrlEncode(username)}&c=1", key);
        using var res = await http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        var arr = JsonSerializer.Deserialize<List<RecentGame>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var game = arr?.FirstOrDefault();
        if (game is not null && !string.IsNullOrEmpty(game.ImageBoxArt))
            game.ImageBoxArt = new Uri(RaMedia, game.ImageBoxArt).ToString();
        return game;
    }

    private static async Task<GameProgress?> GetGameInfoAndUserProgressAsync(HttpClient http, int gameId, string username, string key, CancellationToken ct)
    {
        var url = WithKey($"/API/API_GetGameInfoAndUserProgress.php?g={gameId}&u={WebUtility.UrlEncode(username)}", key);
        using var res = await http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? GetStr(string a, string b) => root.TryGetProperty(a, out var v) ? v.GetString() : (root.TryGetProperty(b, out var v2) ? v2.GetString() : null);

        var title = GetStr("Title", "title") ?? "Unknown";
        var console = GetStr("ConsoleName", "consoleName") ?? "";
        var boxArt = GetStr("ImageBoxArt", "imageBoxArt");
        var total = root.TryGetProperty("NumAchievements", out var na) ? na.GetInt32() : root.GetProperty("numAchievements").GetInt32();
        var earned = root.TryGetProperty("NumAwardedToUser", out var eu) ? eu.GetInt32() : root.GetProperty("numAwardedToUser").GetInt32();
        var percent = total > 0 ? (int)Math.Round((earned / (double)total) * 100) : 0;

        var remaining = new List<RemainingAchievement>();
        if (root.TryGetProperty("Achievements", out var achs) || root.TryGetProperty("achievements", out achs))
        {
            foreach (var prop in achs.EnumerateObject())
            {
                var id = int.TryParse(prop.Name, out var nid) ? nid : 0;
                var a = prop.Value;
                var isEarned = a.TryGetProperty("DateEarned", out var _) || a.TryGetProperty("dateEarned", out _);
                if (!isEarned)
                {
                    var titleA = a.TryGetProperty("Title", out var t) ? t.GetString() : a.GetProperty("title").GetString();
                    var badgeName = a.TryGetProperty("BadgeName", out var bn) ? bn.GetString() : a.GetProperty("badgeName").GetString();
                    var points = a.TryGetProperty("Points", out var p) ? p.GetInt32() : (a.TryGetProperty("points", out p) ? p.GetInt32() : 0);
                    remaining.Add(new RemainingAchievement
                    { Id = id, Title = titleA ?? "", Badge = !string.IsNullOrEmpty(badgeName) ? new Uri(RaMedia, $"/Badge/{badgeName}.png").ToString() : null, Points = points });
                }
            }
        }

        return new GameProgress { GameId = gameId, Title = title, ConsoleName = console, BoxArt = string.IsNullOrEmpty(boxArt) ? null : new Uri(RaMedia, boxArt!).ToString(), Total = total, Earned = earned, Percent = percent, Remaining = remaining };
    }

    private static async Task<List<RecentUnlock>> GetUserRecentAchievementsAsync(HttpClient http, string username, string key, int minutes, CancellationToken ct)
    {
        var url = WithKey($"/API/API_GetUserRecentAchievements.php?u={WebUtility.UrlEncode(username)}&m={minutes}", key);
        using var res = await http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        var arr = JsonSerializer.Deserialize<List<RecentUnlock>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        foreach (var u in arr)
        {
            if (!string.IsNullOrWhiteSpace(u.BadgeUrl) && !u.BadgeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                u.BadgeUrl = new Uri(RaMedia, u.BadgeUrl).ToString();
        }
        return arr;
    }

    private sealed class RecentGame { public int GameId { get; set; } public string Title { get; set; } = ""; public string ConsoleName { get; set; } = ""; public string? ImageBoxArt { get; set; } }
    private sealed class GameProgress { public int GameId { get; set; } public string Title { get; set; } = ""; public string ConsoleName { get; set; } = ""; public string? BoxArt { get; set; } public int Total { get; set; } public int Earned { get; set; } public int Percent { get; set; } public System.Collections.Generic.List<RemainingAchievement> Remaining { get; set; } = new(); }
    private sealed class RemainingAchievement { public int Id { get; set; } public string Title { get; set; } = ""; public string? Badge { get; set; } public int Points { get; set; } }
    private sealed class RecentUnlock { public int Id { get; set; } public string Title { get; set; } = ""; public string Description { get; set; } = ""; public int Points { get; set; } public bool Hardcore { get; set; } public int GameId { get; set; } public string GameTitle { get; set; } = ""; public string? BadgeUrl { get; set; } public string? Date { get; set; } }
}
