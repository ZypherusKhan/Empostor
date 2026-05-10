using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Impostor.Api.Config;
using Impostor.Api.Games;
using Impostor.Api.Games.Managers;
using Impostor.Api.Net;
using Impostor.Api.Net.Manager;
using Impostor.Server.Service.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Impostor.Server.Http
{
    [ApiController]
    public sealed class AdminController : ControllerBase
    {
        private static readonly DateTime StartTime = DateTime.UtcNow;

        private readonly ILogger<AdminController> _logger;
        private readonly IGameManager _gameManager;
        private readonly IClientManager _clientManager;
        private readonly BanStore _bans;
        private readonly AdminConfig _config;
        private readonly ReportStore _reportStore;

        public AdminController(
            ILogger<AdminController> logger,
            IGameManager gameManager,
            IClientManager clientManager,
            BanStore bans,
            IOptions<AdminConfig> config,
            ReportStore reportStore)
        {
            _logger = logger;
            _gameManager = gameManager;
            _clientManager = clientManager;
            _bans = bans;
            _config = config.Value;
            _reportStore = reportStore;
        }

        private bool IsAuthenticated()
            => Request.Cookies.TryGetValue("impostor_admin", out var v) && v == _config.Password;

        [HttpGet("/admin")]
        public IActionResult Panel()
            => Content(IsAuthenticated() ? AdminHtml : LoginHtml, "text/html; charset=utf-8");

        [HttpPost("/admin/login")]
        public IActionResult Login([FromForm] string password)
        {
            if (password != _config.Password)
            {
                _logger.LogWarning("[Admin] Failed login from {Ip}", HttpContext.Connection.RemoteIpAddress);
                return Content(LoginHtml.Replace("<!--ERR-->",
                    "<p style='color:var(--r);margin-top:8px'>Incorrect password.</p>"),
                    "text/html; charset=utf-8");
            }
            Response.Cookies.Append("impostor_admin", password, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromHours(8),
            });
            return Redirect("/admin");
        }

        [HttpPost("/admin/logout")]
        public IActionResult Logout() { Response.Cookies.Delete("impostor_admin"); return Redirect("/admin"); }

        [HttpGet("/api/admin/status")]
        public IActionResult Status()
        {
            if (!IsAuthenticated()) return Unauthorized();
            var up = DateTime.UtcNow - StartTime;
            var games = _gameManager.Games.ToList();
            var (ib, fb) = _bans.Stats();
            return Ok(new
            {
                uptime = Fmt(up), uptimeSeconds = (long)up.TotalSeconds,
                startTime = StartTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
                totalGames = games.Count, totalPlayers = _clientManager.Clients.Count(),
                publicGames = games.Count(g => g.IsPublic),
                activeGames = games.Count(g => g.GameState == GameStates.Started),
                bannedIps = ib, bannedFriendCodes = fb,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                pid = Environment.ProcessId,
            });
        }

        [HttpGet("/api/admin/games")]
        public IActionResult GetGames()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(_gameManager.Games.Select(Snap));
        }

        [HttpGet("/api/admin/clients")]
        public IActionResult GetClients()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(_clientManager.Clients.Select(CSnap));
        }

        [HttpGet("/api/admin/bans")]
        public IActionResult GetBans()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(new { ips = _bans.AllIpBans(), friendCodes = _bans.AllFriendCodeBans() });
        }

        [HttpPost("/api/admin/broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] BroadcastReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Message)) return BadRequest(Err("Message required"));
            var sent = 0;
            foreach (var g in _gameManager.Games)
            {
                var host = g.Host?.Character;
                if (host != null) { await host.SendChatAsync($"[Server] {req.Message}"); sent++; }
            }
            return Ok(new { sent });
        }

        [HttpPost("/api/admin/message")]
        public async Task<IActionResult> GameMessage([FromBody] GameMsgReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var game = FindGame(req.GameCode);
            if (game == null) return NotFound(Err($"Game '{req.GameCode}' not found"));
            var host = game.Host?.Character;
            if (host == null) return BadRequest(Err("No host character"));
            await host.SendChatAsync($"[Admin] {req.Message}");
            return Ok(new { ok = true });
        }

        [HttpPost("/api/admin/kick")]
        public async Task<IActionResult> Kick([FromBody] ClientIdReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var c = FindClient(req.ClientId);
            if (c == null) return NotFound(Err($"Client {req.ClientId} not found"));
            if (c.Player != null) await c.Player.KickAsync();
            else await c.DisconnectAsync(DisconnectReason.Kicked, "Kicked by admin");
            return Ok(new { kicked = true, name = c.Name });
        }

        [HttpPost("/api/admin/ban/ip")]
        public async Task<IActionResult> BanIp([FromBody] BanIpReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (!IPAddress.TryParse(req.Ip, out var ip)) return BadRequest(Err("Invalid IP"));
            var entry = _bans.BanIp(ip, req.Reason ?? "Banned by admin");
            var kicked = 0;
            foreach (var c in _clientManager.Clients.ToList())
            {
                var cip = c.Connection?.EndPoint?.Address;
                if (cip != null && Norm(cip) == Norm(ip))
                {
                    if (c.Player != null) await c.Player.BanAsync();
                    else await c.DisconnectAsync(DisconnectReason.Banned, "Banned by admin");
                    kicked++;
                }
            }
            return Ok(new { banned = entry.Value, disconnected = kicked });
        }

        [HttpPost("/api/admin/ban/fc")]
        public async Task<IActionResult> BanFc([FromBody] BanFcReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.FriendCode)) return BadRequest(Err("FriendCode required"));
            var entry = _bans.BanFriendCode(req.FriendCode, req.Reason ?? "Banned by admin");
            var kicked = 0;
            foreach (var c in _clientManager.Clients.ToList())
            {
                if (c.FriendCode == req.FriendCode)
                {
                    if (c.Player != null) await c.Player.BanAsync();
                    else await c.DisconnectAsync(DisconnectReason.Banned, "Banned by admin");
                    kicked++;
                }
            }
            return Ok(new { banned = entry.Value, disconnected = kicked });
        }

        [HttpPost("/api/admin/unban/ip")]
        public IActionResult UnbanIp([FromBody] UnbanReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(new { removed = _bans.UnbanIp(req.Value) });
        }

        [HttpPost("/api/admin/unban/fc")]
        public IActionResult UnbanFc([FromBody] UnbanReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(new { removed = _bans.UnbanFriendCode(req.Value) });
        }

        [HttpPost("/api/admin/game/end")]
        public async Task<IActionResult> EndGame([FromBody] GameCodeReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var g = FindGame(req.GameCode);
            if (g == null) return NotFound(Err($"Game '{req.GameCode}' not found"));
            var players = g.Players.ToList();
            foreach (var p in players) await p.KickAsync();
            return Ok(new { ended = req.GameCode, playersKicked = players.Count });
        }

        [HttpPost("/api/admin/game/public")]
        public async Task<IActionResult> SetPublic([FromBody] GamePublicReq req)
        {
            if (!IsAuthenticated()) return Unauthorized();
            var g = FindGame(req.GameCode);
            if (g == null) return NotFound(Err($"Game '{req.GameCode}' not found"));
            await g.SetPrivacyAsync(req.IsPublic);
            return Ok(new { gameCode = req.GameCode, isPublic = req.IsPublic });
        }

        [HttpGet("/api/admin/reports")]
        public IActionResult GetReports()
        {
            if (!IsAuthenticated()) return Unauthorized();
            return Ok(_reportStore.GetRecent(200).Select(r => new
            {
                time = r.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                gameCode = r.GameCode,
                reporterName = r.ReporterName,
                reporterFc = r.ReporterFriendCode ?? "—",
                reportedName = r.ReportedName ?? "—",
                reportedFc = r.ReportedFriendCode ?? "—",
                reason = r.Reason.ToString(),
                outcome = r.Outcome.ToString(),
            }));
        }

        private IGame? FindGame(string code)
        {
            try { return _gameManager.Find(new GameCode(code.ToUpperInvariant())); }
            catch { return null; }
        }

        private IClient? FindClient(int id) => _clientManager.Clients.FirstOrDefault(c => c.Id == id);

        private static object Snap(IGame g) => new
        {
            code = GameCodeParser.IntToGameName(g.Code), state = g.GameState.ToString(),
            isPublic = g.IsPublic, playerCount = g.PlayerCount, maxPlayers = g.Options.MaxPlayers,
            map = g.Options.Map.ToString(), impostors = g.Options.NumImpostors,
            host = g.Host?.Client.Name ?? "—", hostFc = g.Host?.Client.FriendCode ?? "—",
            players = g.Players.Select(p => new
            {
                id = p.Client.Id, name = p.Client.Name,
                friendCode = p.Client.FriendCode ?? "—", isHost = p.IsHost,
                platform = p.Client.PlatformSpecificData?.PlatformName ?? "Unknown",
                ip = p.Client.Connection?.EndPoint?.Address?.ToString() ?? "—",
            }).ToList(),
        };

        private static object CSnap(IClient c)
        {
            var reactor = c.GetReactorMods();
            return new
            {
                id = c.Id, name = c.Name, friendCode = c.FriendCode ?? "—",
                gameVersion = c.GameVersion.ToString(),
                platform = c.PlatformSpecificData?.PlatformName ?? "Unknown",
                inGame = c.Player != null,
                gameCode = c.Player != null ? GameCodeParser.IntToGameName(c.Player.Game.Code) : "—",
                ip = c.Connection?.EndPoint?.Address?.ToString() ?? "—",
                reactor = reactor == null ? null : new
                {
                    protocolVersion = reactor.ProtocolVersion,
                    mods = System.Linq.Enumerable.Select(reactor.Mods, m => new
                    {
                        id = m.Id, version = m.Version, required = m.RequiredOnAllClients,
                    }).ToArray(),
                },
            };
        }

        private static string Norm(IPAddress ip)
            => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
        private static object Err(string msg) => new { error = msg };
        private static string Fmt(TimeSpan t)
        {
            if (t.TotalDays >= 1)  return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
            if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
            return $"{t.Minutes}m {t.Seconds}s";
        }

        public sealed record BroadcastReq(string Message);
        public sealed record GameMsgReq(string GameCode, string Message);
        public sealed record ClientIdReq(int ClientId);
        public sealed record BanIpReq(string Ip, string? Reason);
        public sealed record BanFcReq(string FriendCode, string? Reason);
        public sealed record UnbanReq(string Value);
        public sealed record GameCodeReq(string GameCode);
        public sealed record GamePublicReq(string GameCode, bool IsPublic);

        // Usually, we will not supply other languages expect English.
        // But if you really need other language panels, plz create a Pull Request.
        private const string LoginHtml = """
<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Empostor Admin</title>
<style>:root{--bg:#0d1117;--s:#161b22;--b:#30363d;--t:#e6edf3;--m:#7d8590;--a:#2f81f7;--r:#f85149}*{box-sizing:border-box;margin:0;padding:0}body{background:var(--bg);color:var(--t);font:14px/1.5 'Segoe UI',system-ui,sans-serif;min-height:100vh;display:flex;align-items:center;justify-content:center}.card{background:var(--s);border:1px solid var(--b);border-radius:12px;padding:36px 40px;width:340px}h1{font-size:18px;font-weight:700;margin-bottom:24px;text-align:center}label{display:block;font-size:12px;color:var(--m);margin-bottom:5px}input{width:100%;background:#0d1117;border:1px solid var(--b);border-radius:6px;color:var(--t);padding:9px 12px;font-size:14px;outline:none;margin-bottom:14px}input:focus{border-color:var(--a)}button{width:100%;background:var(--a);color:#fff;border:none;border-radius:6px;padding:10px;font-size:14px;font-weight:600;cursor:pointer}button:hover{opacity:.88}</style></head>
<body><div class="card"><h1>🛡 Empostor Admin</h1><form method="POST" action="/admin/login"><label>Password</label><input type="password" name="password" autofocus placeholder="Enter admin password"><button type="submit">Sign in</button><!--ERR--></form></div><div id="cl-modal" style="display:none;position:fixed;inset:0;background:rgba(0,0,0,.7);z-index:200;align-items:center;justify-content:center">
  <div style="background:var(--s);border:1px solid var(--b);border-radius:10px;width:520px;max-width:95vw;max-height:85vh;overflow-y:auto;padding:20px">
    <div style="display:flex;align-items:center;margin-bottom:16px">
      <h2 style="margin:0;flex:1" id="cl-modal-title">Client Detail</h2>
      <button onclick="closeDetail()" style="background:none;border:none;color:var(--m);font-size:18px;cursor:pointer">✕</button>
    </div>
    <div id="cl-modal-body"></div>
  </div>
</div>
</body></html>
""";

        private const string AdminHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Empostor Admin</title>
<style>
:root{--bg:#0d1117;--s:#161b22;--b:#30363d;--t:#e6edf3;--m:#7d8590;--a:#2f81f7;--g:#3fb950;--y:#d29922;--r:#f85149;--p:#bc8cff;--o:#ffa657}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--t);font:14px/1.5 'Segoe UI',system-ui,sans-serif;min-height:100vh;display:flex;flex-direction:column}
header{background:var(--s);border-bottom:1px solid var(--b);padding:0 20px;display:flex;align-items:center;gap:12px;height:52px;position:sticky;top:0;z-index:100}
header h1{font-size:15px;font-weight:700}.dot{width:8px;height:8px;border-radius:50%;background:var(--g);box-shadow:0 0 6px var(--g);flex-shrink:0}
.sp{flex:1}#upd{font-size:11px;color:var(--m)}
.logout{padding:5px 12px;background:rgba(248,81,73,.15);color:var(--r);border:1px solid rgba(248,81,73,.3);border-radius:6px;font-size:12px;cursor:pointer;text-decoration:none}
main{display:flex;flex:1}
nav{width:200px;background:var(--s);border-right:1px solid var(--b);padding:12px 0;flex-shrink:0;position:sticky;top:52px;height:calc(100vh - 52px);overflow-y:auto}
.ni{display:flex;align-items:center;gap:10px;padding:9px 16px;cursor:pointer;color:var(--m);font-size:13px;border-left:3px solid transparent;transition:all .15s}
.ni:hover{color:var(--t);background:rgba(255,255,255,.04)}.ni.active{color:var(--a);border-left-color:var(--a);background:rgba(47,129,247,.08)}
.nsep{margin:8px 16px;border-top:1px solid var(--b)}.nlbl{padding:8px 16px 4px;font-size:11px;color:var(--m);text-transform:uppercase;letter-spacing:.5px}
ct{flex:1;padding:20px;overflow:hidden}
.pnl{display:none}.pnl.active{display:block}
.sr{display:grid;grid-template-columns:repeat(auto-fill,minmax(160px,1fr));gap:10px;margin-bottom:20px}
.sc{background:var(--s);border:1px solid var(--b);border-radius:8px;padding:14px 18px}
.sc .lbl{color:var(--m);font-size:11px;text-transform:uppercase;letter-spacing:.5px;margin-bottom:5px}.sc .val{font-size:26px;font-weight:700}.sc .sub{font-size:11px;color:var(--m);margin-top:3px}
h2{font-size:14px;font-weight:600;margin-bottom:14px;color:var(--m);text-transform:uppercase;letter-spacing:.5px}
table{width:100%;border-collapse:collapse}th{text-align:left;padding:7px 10px;color:var(--m);font-size:11px;text-transform:uppercase;letter-spacing:.5px;border-bottom:1px solid var(--b);font-weight:500}
td{padding:9px 10px;border-bottom:1px solid var(--b);vertical-align:top}tr:hover td{background:rgba(255,255,255,.025)}
.code{font-family:monospace;color:var(--a);font-weight:700;letter-spacing:1px}.fc{color:var(--p);font-size:11px}.ip{color:var(--m);font-size:11px;font-family:monospace}
.badge{display:inline-flex;align-items:center;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600}
.bs{background:rgba(63,185,80,.15);color:var(--g)}.bn{background:rgba(48,54,61,.8);color:var(--m)}.by{background:rgba(210,153,34,.2);color:var(--y)}.be{background:rgba(248,81,73,.15);color:var(--r)}.bpub{background:rgba(63,185,80,.12);color:var(--g)}.bprv{background:rgba(125,133,144,.12);color:var(--m)}
.chips{display:flex;flex-wrap:wrap;gap:3px}.chip{background:rgba(47,129,247,.1);border:1px solid rgba(47,129,247,.2);border-radius:20px;padding:1px 8px;font-size:11px;color:var(--a)}.chip.host{background:rgba(255,166,87,.1);border-color:rgba(255,166,87,.25);color:var(--o)}
.form{background:var(--s);border:1px solid var(--b);border-radius:8px;padding:16px;margin-bottom:16px}.form h3{font-size:13px;font-weight:600;margin-bottom:12px;color:var(--t)}
.field{margin-bottom:10px}.field label{display:block;font-size:12px;color:var(--m);margin-bottom:4px}
input,select,textarea{width:100%;background:#0d1117;border:1px solid var(--b);border-radius:6px;color:var(--t);padding:7px 10px;font-size:13px;outline:none;font-family:inherit}
input:focus,select:focus,textarea:focus{border-color:var(--a)}textarea{resize:vertical;min-height:60px}
.row{display:flex;gap:8px}.row input,.row select{flex:1}
button{display:inline-flex;align-items:center;gap:6px;padding:7px 14px;border-radius:6px;font-size:13px;font-weight:500;cursor:pointer;border:none;transition:opacity .15s}
button:hover{opacity:.85}.bp{background:var(--a);color:#fff}.bd{background:var(--r);color:#fff}.bw{background:var(--y);color:#000}.bsm{padding:4px 10px;font-size:12px}
.msg{padding:8px 12px;border-radius:6px;font-size:12px;margin-top:8px;display:none}
.msg.ok{background:rgba(63,185,80,.15);color:var(--g);border:1px solid rgba(63,185,80,.3)}.msg.err{background:rgba(248,81,73,.12);color:var(--r);border:1px solid rgba(248,81,73,.3)}
.empty{text-align:center;padding:40px;color:var(--m)}
.ig{display:grid;grid-template-columns:200px 1fr;gap:0;background:var(--s);border:1px solid var(--b);border-radius:8px;overflow:hidden}
.ik,.iv{padding:8px 14px;border-bottom:1px solid var(--b)}.ik{color:var(--m);font-size:12px}.iv{font-family:monospace;font-size:12px}
.bi{display:flex;align-items:center;gap:10px;padding:8px 12px;background:var(--s);border:1px solid var(--b);border-radius:6px;margin-bottom:6px}
.bv{flex:1;font-family:monospace;font-size:13px}.br2{font-size:11px;color:var(--m)}.bt{font-size:11px;color:var(--m);margin-left:auto}
</style>
</head>
<body>
<header><div class="dot" id="dot"></div><h1>Empostor Admin</h1><span class="sp"></span><span id="upd"></span>
<form method="POST" action="/admin/logout" style="margin:0"><button class="logout" type="submit">Sign out</button></form>
</header>
<main>
<nav>
  <div class="nlbl">Monitor</div>
  <div class="ni active" onclick="nav('ov')">📊 Overview</div>
  <div class="ni" onclick="nav('gm')">🎮 Games</div>
  <div class="ni" onclick="nav('cl')">👥 Clients</div>
  <div class="nsep"></div>
  <div class="nlbl">Actions</div>
  <div class="ni" onclick="nav('bc')">📢 Broadcast</div>
  <div class="ni" onclick="nav('ki')">🚪 Kick</div>
  <div class="ni" onclick="nav('ba')">🔨 Ban</div>
  <div class="ni" onclick="nav('bl')">📋 Ban List</div>
  <div class="nsep"></div>
  <div class="nlbl">Game Control</div>
  <div class="ni" onclick="nav('ms')">💬 Message</div>
  <div class="ni" onclick="nav('ge')">⛔ End Game</div>
  <div class="ni" onclick="nav('gp')">🌐 Privacy</div>
  <div class="nsep"></div>
  <div class="nlbl">Extend</div>
  <div class="ni" onclick="nav('mk')">🧩 Marketplace</div>
  <div class="ni" onclick="nav('ud')">🔄 Updates</div>
  <div class="nsep"></div>
  <div class="nlbl">System</div>
  <div class="ni" onclick="nav('rp')">📋 Reports</div>
  <div class="ni" onclick="nav('si')">⚙️ Server Info</div>
</nav>
<ct>
<div id="p-ov" class="pnl active">
  <div class="sr">
    <div class="sc"><div class="lbl">Games</div><div class="val" id="s1">—</div><div class="sub" id="s1b"></div></div>
    <div class="sc"><div class="lbl">Active</div><div class="val" id="s2">—</div></div>
    <div class="sc"><div class="lbl">Players</div><div class="val" id="s3">—</div></div>
    <div class="sc"><div class="lbl">Bans</div><div class="val" id="s4">—</div><div class="sub" id="s4b"></div></div>
    <div class="sc"><div class="lbl">Uptime</div><div class="val" id="s5" style="font-size:16px">—</div></div>
  </div>
  <h2>Active Games</h2>
  <table><thead><tr><th>Code</th><th>State</th><th>Visibility</th><th>Map</th><th>Players</th><th>Host</th></tr></thead>
  <tbody id="ov-t"><tr><td colspan="6" class="empty">Loading…</td></tr></tbody></table>
</div>
<div id="p-gm" class="pnl"><h2>All Games</h2>
  <table><thead><tr><th>Code</th><th>State</th><th>Visibility</th><th>Map</th><th>Players</th><th>Host / FC</th><th>Members</th></tr></thead>
  <tbody id="gm-t"></tbody></table>
</div>
<div id="p-cl" class="pnl"><h2>Clients</h2>
  <table><thead><tr><th>ID</th><th>Name</th><th>Friend Code</th><th>IP</th><th>Version</th><th>Platform</th><th>Mods</th><th>In Game</th></tr></thead>
  <tbody id="cl-t"></tbody></table>
</div>
<div id="p-bc" class="pnl"><div class="form"><h3>📢 Broadcast to All Games</h3>
  <div class="field"><label>Message</label><textarea id="bc-m" placeholder="Message…"></textarea></div>
  <button class="bp" onclick="doBc()">Send to All Games</button><div id="bc-r" class="msg"></div>
</div></div>
<div id="p-ki" class="pnl">
  <div class="form"><h3>🚪 Kick by Client ID</h3>
    <div class="field"><label>Client ID</label><input id="ki-id" type="number" placeholder="Client ID"></div>
    <button class="bw" onclick="doKick()">Kick</button><div id="ki-r" class="msg"></div>
  </div>
  <div class="form"><h3>Quick Kick</h3>
    <table><thead><tr><th>ID</th><th>Name</th><th>FC</th><th>Game</th><th></th></tr></thead><tbody id="ki-t"></tbody></table>
  </div>
</div>
<div id="p-ba" class="pnl"><div style="display:grid;grid-template-columns:1fr 1fr;gap:16px">
  <div class="form"><h3>🔨 Ban IP</h3>
    <div class="field"><label>IP</label><input id="bi-v" placeholder="1.2.3.4"></div>
    <div class="field"><label>Reason</label><input id="bi-r" placeholder="Optional…"></div>
    <button class="bd" onclick="doBanIp()">Ban IP</button><div id="bi-msg" class="msg"></div>
  </div>
  <div class="form"><h3>🔨 Ban Friend Code</h3>
    <div class="field"><label>Friend Code</label><input id="bf-v" placeholder="Name#1234"></div>
    <div class="field"><label>Reason</label><input id="bf-r" placeholder="Optional…"></div>
    <button class="bd" onclick="doBanFc()">Ban FC</button><div id="bf-msg" class="msg"></div>
  </div>
</div></div>
<div id="p-bl" class="pnl"><div style="display:grid;grid-template-columns:1fr 1fr;gap:16px">
  <div><h2>Banned IPs</h2><div id="bl-ip"></div></div>
  <div><h2>Banned Friend Codes</h2><div id="bl-fc"></div></div>
</div></div>
<div id="p-ms" class="pnl"><div class="form"><h3>💬 Message to Game</h3>
  <div class="row">
    <div class="field" style="flex:0 0 140px"><label>Game Code</label><input id="ms-c" placeholder="ABCDEF" style="text-transform:uppercase"></div>
    <div class="field" style="flex:1"><label>Message</label><input id="ms-m" placeholder="Message…"></div>
  </div>
  <button class="bp" onclick="doMsg()">Send</button><div id="ms-r" class="msg"></div>
</div></div>
<div id="p-ge" class="pnl">
  <div class="form"><h3>⛔ Force End Game</h3>
    <div class="field"><label>Game Code</label><input id="ge-c" placeholder="ABCDEF" style="text-transform:uppercase"></div>
    <button class="bd" onclick="doEnd()">End Game</button><div id="ge-r" class="msg"></div>
  </div>
  <div class="form"><h3>Active Games</h3>
    <table><thead><tr><th>Code</th><th>State</th><th>Players</th><th></th></tr></thead><tbody id="ge-t"></tbody></table>
  </div>
</div>
<div id="p-gp" class="pnl"><div class="form"><h3>🌐 Set Game Privacy</h3>
  <div class="row">
    <div class="field" style="flex:0 0 140px"><label>Code</label><input id="gp-c" placeholder="ABCDEF" style="text-transform:uppercase"></div>
    <div class="field" style="flex:0 0 140px"><label>Visibility</label><select id="gp-v"><option value="true">Public</option><option value="false">Private</option></select></div>
  </div>
  <button class="bp" onclick="doPrivacy()">Apply</button><div id="gp-r" class="msg"></div>
</div></div>
<div id="p-mk" class="pnl">
  <div style="display:flex;align-items:center;gap:10px;margin-bottom:16px"><h2 style="margin:0">🧩 Plugin Marketplace</h2><button class="bp bsm" onclick="fMarket()">Refresh</button></div>
  <div id="mk-list"><div class="empty">Loading…</div></div>
</div>
<div id="p-ud" class="pnl"><div class="form" style="max-width:480px"><h3>🔄 Server Update Check</h3>
  <div id="ud-box"><div class="empty">Click Check to query GitHub.</div></div>
  <button class="bp" style="margin-top:12px" onclick="fUpdate()">Check for Updates</button>
</div></div>
<div id="p-rp" class="pnl">
  <div style="display:flex;align-items:center;gap:10px;margin-bottom:14px">
    <h2 style="margin:0">📋 Player Reports</h2>
    <button class="bp bsm" onclick="fReports()">Refresh</button>
  </div>
  <table><thead><tr><th>Time</th><th>Game</th><th>Reporter</th><th>Reported</th><th>Reason</th><th>Outcome</th></tr></thead>
  <tbody id="rp-t"><tr><td colspan="6" class="empty">Loading…</td></tr></tbody></table>
</div>
<div id="p-si" class="pnl"><h2>Server Info</h2><div class="ig" id="si-d"></div></div>
</ct>
</main>
<script>
let cur='ov';
function nav(id){document.querySelectorAll('.ni').forEach(e=>e.classList.remove('active'));event.currentTarget.classList.add('active');document.querySelectorAll('.pnl').forEach(e=>e.classList.remove('active'));document.getElementById('p-'+id).classList.add('active');cur=id;refreshTab();}
async function api(m,p,b){const o={method:m,headers:{'Content-Type':'application/json'}};if(b)o.body=JSON.stringify(b);const r=await fetch(p,o);if(r.status===401){location.reload();return{ok:false,data:{}};}return{ok:r.ok,data:await r.json()};}
function msg(id,ok,t){const e=document.getElementById(id);e.className='msg '+(ok?'ok':'err');e.textContent=t;e.style.display='block';setTimeout(()=>e.style.display='none',4000)}
function e(s){return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;')}
function sc(s){return{Started:'bs',NotStarted:'bn',Starting:'by',Ended:'be'}[s]||'bn'}
async function fetchStatus(){
  try{
    const{data:d}=await api('GET','/api/admin/status');
    document.getElementById('s1').textContent=d.totalGames;document.getElementById('s1b').textContent=d.publicGames+' public';
    document.getElementById('s2').textContent=d.activeGames;document.getElementById('s3').textContent=d.totalPlayers;
    document.getElementById('s4').textContent=d.bannedIps+d.bannedFriendCodes;document.getElementById('s4b').textContent=d.bannedIps+' IPs · '+d.bannedFriendCodes+' FCs';
    document.getElementById('s5').textContent=d.uptime;
    document.getElementById('upd').textContent='Updated '+new Date().toLocaleTimeString();
    document.getElementById('dot').style.background='var(--g)';
    document.getElementById('si-d').innerHTML=[['Started',d.startTime],['Uptime',d.uptime],['PID',d.pid],['Runtime',d.runtime],['OS',d.os],['Bans',d.bannedIps+' IPs, '+d.bannedFriendCodes+' FCs']].map(([k,v])=>`<div class="ik">${e(k)}</div><div class="iv">${e(v)}</div>`).join('');
  }catch{document.getElementById('dot').style.background='var(--r)';}
}
async function fGames(tid,short){const{data:gs}=await api('GET','/api/admin/games');const tb=document.getElementById(tid);if(!gs.length){tb.innerHTML=`<tr><td colspan="${short?6:7}" class="empty">No games</td></tr>`;return;}tb.innerHTML=gs.map(g=>`<tr><td><span class="code">${e(g.code)}</span></td><td><span class="badge ${sc(g.state)}">${e(g.state)}</span></td><td><span class="badge ${g.isPublic?'bpub':'bprv'}">${g.isPublic?'Public':'Private'}</span></td><td>${e(g.map)}</td><td>${g.playerCount}/${g.maxPlayers}</td><td>${e(g.host)}<br><span class="fc">${e(g.hostFc)}</span></td>${short?'':'<td><div class="chips">'+g.players.map(p=>`<span class="chip${p.isHost?' host':''}" title="${e(p.friendCode)}\n${e(p.ip)}">${e(p.name)}</span>`).join('')+'</div></td>'}</tr>`).join('');}
async function fClients(){const{data:cs}=await api('GET','/api/admin/clients');const tb=document.getElementById('cl-t');if(!cs.length){tb.innerHTML='<tr><td colspan="7" class="empty">No clients</td></tr>';return;}tb.innerHTML=cs.map(c=>{
  let modsHtml='<span style="color:var(--m)">—</span>';
  if(c.reactor&&c.reactor.mods&&c.reactor.mods.length){
    modsHtml=`<span style="font-size:11px;color:var(--p)" title="${e(c.reactor.mods.map(m=>m.id+' '+m.version).join('\n'))}">${c.reactor.mods.length} mod(s)</span>`;
  }
  return`<tr><td style="color:var(--m)">${c.id}</td><td>${e(c.name)}</td><td><span class="fc">${e(c.friendCode)}</span></td><td><span class="ip">${e(c.ip)}</span></td><td>${e(c.gameVersion)}</td><td>${e(c.platform)}</td><td>${modsHtml}</td><td>${c.inGame?`<span class="code">${e(c.gameCode)}</span>`:'<span style="color:var(--m)">Lobby</span>'}</td></tr>`;
}).join('');}
async function fKickList(){const{data:cs}=await api('GET','/api/admin/clients');const tb=document.getElementById('ki-t');if(!cs.length){tb.innerHTML='<tr><td colspan="5" class="empty">No clients</td></tr>';return;}tb.innerHTML=cs.map(c=>`<tr><td style="color:var(--m)">${c.id}</td><td>${e(c.name)}</td><td><span class="fc">${e(c.friendCode)}</span></td><td>${c.inGame?`<span class="code">${e(c.gameCode)}</span>`:'—'}</td><td><button class="bw bsm" onclick="qkick(${c.id})">Kick</button></td></tr>`).join('');}
async function fBans(){const{data:d}=await api('GET','/api/admin/bans');document.getElementById('bl-ip').innerHTML=d.ips.length?d.ips.map(b=>bi(b,'ip')).join(''):'<div class="empty">None</div>';document.getElementById('bl-fc').innerHTML=d.friendCodes.length?d.friendCodes.map(b=>bi(b,'fc')).join(''):'<div class="empty">None</div>';}
function bi(b,t){return`<div class="bi"><div><div class="bv">${e(b.value)}</div><div class="br2">${e(b.reason)}</div></div><div class="bt">${new Date(b.bannedAt).toLocaleString()}</div><button class="bsm" style="background:rgba(248,81,73,.15);color:var(--r);border:1px solid rgba(248,81,73,.3)" onclick="doUnban('${t}','${e(b.value)}')">Unban</button></div>`}
async function fGamesEnd(){const{data:gs}=await api('GET','/api/admin/games');const tb=document.getElementById('ge-t');if(!gs.length){tb.innerHTML='<tr><td colspan="4" class="empty">No games</td></tr>';return;}tb.innerHTML=gs.map(g=>`<tr><td><span class="code">${e(g.code)}</span></td><td><span class="badge ${sc(g.state)}">${e(g.state)}</span></td><td>${g.playerCount}/${g.maxPlayers}</td><td><button class="bd bsm" onclick="qend('${e(g.code)}')">End</button></td></tr>`).join('');}
async function fMarket(){const el=document.getElementById('mk-list');el.innerHTML='<div class="empty">Loading…</div>';try{const{ok,data}=await api('GET','/api/admin/marketplace/plugins');if(!ok){el.innerHTML=`<div class="empty" style="color:var(--r)">${e(data.error??'Error')}</div>`;return;}if(!data.length){el.innerHTML='<div class="empty">No plugins.</div>';return;}el.innerHTML=data.map(p=>{const lat=p.versions?.length?p.versions[p.versions.length-1]:null;const ver=lat?.version??'—';const imp=lat?.impostor_version??'—';const url=lat?.download_url;return`<div class="form" style="margin-bottom:10px"><div style="display:flex;align-items:flex-start;gap:12px"><div style="flex:1"><div style="font-weight:600;font-size:14px">${e(p.name)}</div><div style="font-size:12px;color:var(--m);margin:4px 0">${e(p.description)}</div><div style="font-size:11px;color:var(--m)">By ${e(p.author)} · v${e(ver)} · Empostor ${e(imp)}</div></div>${url?`<button class="bp bsm" style="flex-shrink:0" onclick="install('${e(url)}',this)">Install</button>`:''}</div><div class="install-msg" style="font-size:12px;margin-top:6px;display:none"></div></div>`;}).join('');}catch(err){el.innerHTML=`<div class="empty" style="color:var(--r)">${e(String(err))}</div>`;}}
async function install(url,btn){btn.disabled=true;btn.textContent='Installing…';const msgEl=btn.closest('.form').querySelector('.install-msg');const{ok,data}=await api('POST','/api/admin/marketplace/install',{downloadUrl:url});if(ok){btn.textContent='Installed';msgEl.style.display='block';msgEl.style.color='var(--g)';msgEl.textContent='✓ Installed. Restart server to enable.';}else{btn.disabled=false;btn.textContent='Install';msgEl.style.display='block';msgEl.style.color='var(--r)';msgEl.textContent='✗ '+(data.error??'Error');}}
async function fUpdate(){const box=document.getElementById('ud-box');box.innerHTML='<div class="empty">Checking…</div>';const{ok,data}=await api('GET','/api/admin/update/check');if(!ok){box.innerHTML=`<div class="empty" style="color:var(--r)">${e(data.error??'Error')}</div>`;return;}const badge=data.upToDate?'<span class="badge bs">Up to date</span>':'<span class="badge be">Update available</span>';box.innerHTML=`<div class="ig" style="border-radius:6px;overflow:hidden"><div class="ik">Current</div><div class="iv">${e(data.currentVersion)}</div><div class="ik">Latest</div><div class="iv">${e(data.latestVersion)} ${badge}</div><div class="ik">Release</div><div class="iv"><a href="${e(data.releaseUrl)}" target="_blank" style="color:var(--a)">${e(data.latestName)}</a></div></div>${!data.upToDate?'<p style="font-size:12px;color:var(--y);margin-top:10px">⚠ A new version is available. Update manually.</p>':''}`;}
function refreshTab(){if(cur==='ov')fGames('ov-t',true);if(cur==='gm')fGames('gm-t',false);if(cur==='cl')fClients();if(cur==='ki')fKickList();if(cur==='bl')fBans();if(cur==='ge')fGamesEnd();}
async function doBc(){const m=document.getElementById('bc-m').value.trim();if(!m)return msg('bc-r',false,'Message required');const{ok,data}=await api('POST','/api/admin/broadcast',{message:m});msg('bc-r',ok,ok?`Sent to ${data.sent} game(s)`:(data.error??'Error'));}
async function doKick(){const id=parseInt(document.getElementById('ki-id').value);if(!id)return msg('ki-r',false,'Enter client ID');const{ok,data}=await api('POST','/api/admin/kick',{clientId:id});msg('ki-r',ok,ok?`Kicked ${data.name}`:(data.error??'Error'));if(ok)fKickList();}
async function qkick(id){const{ok,data}=await api('POST','/api/admin/kick',{clientId:id});if(!ok)alert(data.error??'Error');fKickList();}
async function doBanIp(){const v=document.getElementById('bi-v').value.trim(),r=document.getElementById('bi-r').value.trim();if(!v)return msg('bi-msg',false,'IP required');const{ok,data}=await api('POST','/api/admin/ban/ip',{ip:v,reason:r});msg('bi-msg',ok,ok?`Banned ${data.banned} (${data.disconnected} disconnected)`:(data.error??'Error'));}
async function doBanFc(){const v=document.getElementById('bf-v').value.trim(),r=document.getElementById('bf-r').value.trim();if(!v)return msg('bf-msg',false,'FC required');const{ok,data}=await api('POST','/api/admin/ban/fc',{friendCode:v,reason:r});msg('bf-msg',ok,ok?`Banned ${data.banned} (${data.disconnected} disconnected)`:(data.error??'Error'));}
async function doUnban(t,v){await api('POST',`/api/admin/unban/${t}`,{value:v});fBans();}
async function doMsg(){const c=document.getElementById('ms-c').value.trim().toUpperCase(),m=document.getElementById('ms-m').value.trim();if(!c||!m)return msg('ms-r',false,'Both required');const{ok,data}=await api('POST','/api/admin/message',{gameCode:c,message:m});msg('ms-r',ok,ok?'Sent':(data.error??'Error'));}
async function doEnd(){const c=document.getElementById('ge-c').value.trim().toUpperCase();if(!c)return msg('ge-r',false,'Code required');if(!confirm(`End game ${c}?`))return;const{ok,data}=await api('POST','/api/admin/game/end',{gameCode:c});msg('ge-r',ok,ok?`Ended (${data.playersKicked} kicked)`:(data.error??'Error'));if(ok)fGamesEnd();}
async function qend(c){if(!confirm(`End ${c}?`))return;await api('POST','/api/admin/game/end',{gameCode:c});fGamesEnd();}
async function doPrivacy(){const c=document.getElementById('gp-c').value.trim().toUpperCase(),p=document.getElementById('gp-v').value==='true';if(!c)return msg('gp-r',false,'Code required');const{ok,data}=await api('POST','/api/admin/game/public',{gameCode:c,isPublic:p});msg('gp-r',ok,ok?`${c} → ${p?'public':'private'}`:(data.error??'Error'));}
async function fReports(){
  const{data:rs}=await api('GET','/api/admin/reports');
  const tb=document.getElementById('rp-t');
  if(!rs.length){tb.innerHTML='<tr><td colspan="6" class="empty">No reports yet.</td></tr>';return;}
  const rColor={Cheating_Hacking:'var(--r)',Harassment_Misconduct:'var(--y)',InappropriateName:'var(--m)',InappropriateChat:'var(--m)'};
  tb.innerHTML=rs.map(r=>`<tr>
    <td style="font-size:11px;color:var(--m);white-space:nowrap">${e(r.time)}</td>
    <td><span class="code" style="font-size:11px">${e(r.gameCode)}</span></td>
    <td><b>${e(r.reporterName)}</b><br><span class="fc">${e(r.reporterFc)}</span></td>
    <td><b>${e(r.reportedName)}</b><br><span class="fc">${e(r.reportedFc)}</span></td>
    <td><span style="color:${rColor[r.reason]??'var(--t)';font-size:12px">${e(r.reason.replace('_',' '))}</span></td>
    <td><span style="font-size:12px;color:${r.outcome==='Reported'?'var(--g)':'var(--m)'}">${e(r.outcome)}</span></td>
  </tr>`).join('');
}
function showDetail(clientJson){
  const c=JSON.parse(clientJson);
  document.getElementById('cl-modal-title').textContent=c.name+' — Detail';
  let body=`<div class="ig" style="border-radius:6px;overflow:hidden;margin-bottom:14px">
    <div class="ik">Name</div><div class="iv">${e(c.name)}</div>
    <div class="ik">Friend Code</div><div class="iv">${e(c.friendCode)}</div>
    <div class="ik">PUID</div><div class="iv">${e(c.puid||'—')}</div>
    <div class="ik">IP</div><div class="iv">${e(c.ip)}</div>
    <div class="ik">Client ID</div><div class="iv">${c.id}</div>
    <div class="ik">Version</div><div class="iv">${e(c.gameVersion)}</div>
    <div class="ik">Platform</div><div class="iv">${e(c.platform)}</div>
    <div class="ik">Language</div><div class="iv">${e(c.language||'—')}</div>
    <div class="ik">In Game</div><div class="iv">${c.inGame?'<span class="code">'+e(c.gameCode)+'</span>':'No'}</div>
  </div>`;
  if(c.reactor){
    body+=`<h3 style="font-size:13px;color:var(--m);text-transform:uppercase;letter-spacing:.5px;margin-bottom:10px">🧩 Reactor Mods (${c.reactor.mods.length})</h3>`;
    if(c.reactor.mods.length){
      body+=`<table><thead><tr><th>Mod ID</th><th>Version</th><th>Required</th></tr></thead><tbody>`;
      body+=c.reactor.mods.map(m=>`<tr><td style="font-family:monospace;font-size:12px">${e(m.id)}</td><td style="font-size:12px">${e(m.version)}</td><td style="font-size:12px">${m.required?'<span style="color:var(--y)">Yes</span>':'No'}</td></tr>`).join('');
      body+=`</tbody></table>`;
      body+=`<div style="font-size:11px;color:var(--m);margin-top:6px">Protocol: ${e(c.reactor.protocolVersion)}</div>`;
    } else {
      body+=`<div style="font-size:12px;color:var(--m)">No mods.</div>`;
    }
  }
  document.getElementById('cl-modal-body').innerHTML=body;
  const modal=document.getElementById('cl-modal');
  modal.style.display='flex';
  modal.onclick=ev=>{if(ev.target===modal)closeDetail();};
}
function closeDetail(){document.getElementById('cl-modal').style.display='none';}
fetchStatus();setInterval(fetchStatus,5000);setInterval(()=>{if(document.visibilityState==='visible')refreshTab();},3000);document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible')fetchStatus();});refreshTab();
</script>
<!-- Privacy Policy Overlay -->
<div id="privacy-overlay" style="position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.75);z-index:99999;display:none;align-items:center;justify-content:center;">
  <div style="background:var(--s);border:1px solid var(--b);border-radius:12px;max-width:720px;width:90%;max-height:85%;display:flex;flex-direction:column;padding:24px;">
    <h2 style="margin:0 0 12px 0;color:var(--t);font-size:18px;">Privacy Policy for this Server</h2>
    <div id="privacy-content" style="flex:1;overflow-y:auto;padding-right:8px;color:var(--t);font-size:13px;line-height:1.6;white-space:pre-wrap;">
      <!-- filled by script -->
    </div>
    <div style="margin-top:16px;display:flex;align-items:center;gap:12px;border-top:1px solid var(--b);padding-top:14px;">
      <label style="color:var(--m);font-size:12px;display:flex;align-items:center;gap:6px;">
        <input type="checkbox" id="privacy-agree-check" style="width:auto;accent-color:var(--a);">
        I have read and understand this privacy policy, and I will ensure it is accessible to all players on my server.
      </label>
      <button id="privacy-confirm-btn" style="margin-left:auto;background:var(--a);color:#fff;border:none;border-radius:6px;padding:8px 20px;font-size:14px;font-weight:600;cursor:pointer;opacity:0.5;pointer-events:none;" disabled>Confirm</button>
    </div>
  </div>
</div>

<script>
(function() {
  // If already agreed, skip overlay
  if (document.cookie.split('; ').find(row => row.startsWith('privacy_agreed='))) return;

  const privacyText = `Privacy Policy for this Private Server

1. Introduction
The server operator is committed to protecting player privacy. This privacy policy explains how we collect, use, store, and safeguard player information while operating this private Among Us server.

This document must be made clearly visible to all players who join your server, so that every player understands how their data is handled before they start playing.

2. Types of Information Collected
When a player joins and uses this server, the following information is automatically collected:
- IP address – used for server connection, security protection, and abuse prevention.
- In-game friend code – used for identity verification and in-server permission management.
- In-game chat messages – including all content sent in the game chat channels.
- Player name – the display name shown in the game.

3. Purpose of Information Use
The information collected is used solely for the following purposes:
- Ensuring normal server operation and stable connections.
- Maintaining server order, preventing cheating, harassment, or other rule-breaking behavior.
- Investigating violations of server rules when necessary.
- Improving server management and player experience.

4. Information Storage and Protection
All collected information is stored only in protected server environments, with access restricted to authorized members of the server operator's management team.
Reasonable technical measures are taken to prevent information leakage, tampering, or unauthorized access.
Unless required by law or in response to a security incident, we will not proactively provide or disclose your information to any third party.

5. Information Retention Period
Unless needed for violation investigations or legal compliance, player information will be deleted within a reasonable period after the player ceases using the server.

6. User Rights
Players have the following rights:
- To ask whether the server holds their information and how it is used.
- To request deletion of their information (unless retention is required by law).
- To refuse collection of certain information, though this may result in inability to use the server.

For any related requests or inquiries, players should contact us.

7. Information Sharing and Disclosure
We do not sell, rent, or trade player information to any third party. Disclosure may only occur in the following extremely limited circumstances:
- When required by mandatory laws, regulations, judicial or administrative authorities.
- To protect the safety, rights, and property of the server operator, other players, or the public, such as in cases of investigating fraud or malicious attacks.

8. Privacy Policy Updates
This policy may be updated from time to time. The updated version will be published on the server announcement or relevant page. Continued use of the server constitutes acceptance of the revised policy.

9. Disclaimer
Please note that the Among Us game itself is developed by Innersloth and is subject to its own Terms of Service and Privacy Policy. This policy applies only to the management practices of this server.

10. Contact Us
If players have any questions about this privacy policy or how their data is handled, please contact us.

Note: As this is a volunteer-operated private server, responses may take some time. We appreciate your patience.

Notice to the Server Operator:
I am providing this privacy policy text to you, the server operator. It is your responsibility to post this policy in a location that is easily and prominently accessible to all players — for example, on your server welcome page, in a dedicated announcement channel, or on a publicly visible notice board.
You must require that all players read and acknowledge this policy before joining the game. Protecting player privacy is not just a legal and ethical duty; it also helps build trust in your server community. If you have any questions about implementing this policy or explaining it to your players, please reach out to me.`;

  document.getElementById('privacy-content').textContent = privacyText;
  const overlay = document.getElementById('privacy-overlay');
  const check = document.getElementById('privacy-agree-check');
  const btn = document.getElementById('privacy-confirm-btn');

  overlay.style.display = 'flex'; // show overlay

  check.addEventListener('change', () => {
    btn.style.opacity = check.checked ? '1' : '0.5';
    btn.style.pointerEvents = check.checked ? 'auto' : 'none';
    btn.disabled = !check.checked;
  });

  btn.addEventListener('click', () => {
    if (!check.checked) return;
    // Set cookie to remember consent (1 year)
    document.cookie = "privacy_agreed=1; path=/; max-age=" + 60*60*24*365 + "; SameSite=Lax";
    overlay.style.display = 'none';
  });
})();
</script>
</body></html>
""";
    }
}
