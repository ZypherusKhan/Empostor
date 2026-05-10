# Empostor

[![Discord](https://img.shields.io/badge/Discord-chat-blue?style=flat-square)](https://discord.gg/5fPmpxxnrc)
[![QQ](https://img.shields.io/badge/QQ-Group-black?style=flat-square)](https://qm.qq.com/q/GeX3Q0Ft0k)
[![License](https://img.shields.io/badge/License-GPLv3-green?style=flat-square)](LICENSE)

**Empostor** is a feature-rich, open-source private server for Among Us, built on top of [Impostor](https://github.com/Impostor/Impostor) with significant enhancements.

> For the latest Among Us version, use builds from the `master` branch.  
> For older versions, the upstream [Impostor releases](https://github.com/Impostor/Impostor/releases/) may be used, but with limited support.

---

## Features

| Feature | Description |
|---------|-------------|
| 🎮 Full AU Compatibility | Complete implementation of all Among Us game mechanics |
| 🔌 Plugin System | Load/unload plugins with per-plugin config file generation |
| 🏪 Plugin Marketplace | Install plugins directly from GitHub via the admin panel |
| 🛡 Admin Panel | Web-based management at `/admin` with login protection |
| 📊 Reports Dashboard | View and track player reports with reason and outcome |
| 🔨 Ban System | Persistent IP and Friend Code bans surviving server restarts |
| 💬 Command Framework | Built-in `/help`, `/setcolor`, `/weartitle`, `/note`; extensible via `ICommand` in `Impostor.Api` |
| 🌐 16-Language Support | Server messages localised based on each player's client language |
| 🔒 Friend Code Validation | Kicks clients with invalid/spoofed friend codes |
| 🧩 Reactor Mod Detection | Reads connected clients' Reactor mod list, visible in admin panel |
| 📌 Fixed Room Codes | Assign a permanent room code to a specific host's friend code |
| 🏷 Title System | Assign display title prefixes via config or `/weartitle` command |
| 🔐 HTTP Auth | EOS token → real-time Innersloth API → `matchmakerToken` auth flow |
| 🐳 Docker Ready | Multi-stage Dockerfile + `docker-compose.yml` included |

---

## Quick Start

### Play on someone's server

Configure your Among Us client to connect to an Empostor server using the [region file generator](https://impostor.github.io/Impostor).

### Host your own server

See **[docs/Running-the-server.md](docs/Running-the-server.md)** for full setup instructions.

For the admin panel, see **[docs/Admin-panel.md](docs/Admin-panel.md)**.

For the home page, see **[docs/Hello-page.md](docs/Hello-page.md)**.

#### Docker (recommended)

```bash
# 1. Set your public IP
export PUBLIC_IP=1.2.3.4

# 2. Edit the admin password
nano data/config.json   # change Admin.Password

# 3. Start
docker compose up -d

# 4. Open admin panel
# http://your-server:22023/admin
```

#### Manual

```bash
dotnet publish src/Core/Impostor.Server/Impostor.Server.csproj -c Release -o ./publish
cd publish
./Impostor.Server
```

---

## Plugin Marketplace

The marketplace reads from a GitHub raw JSON URL (configurable in `config.json` under `Admin.MarketplaceUrl`). No separate server required — just host a `plugins.json` in any GitHub repo.

See **[marketplace/plugins.json](marketplace/plugins.json)** for the format.

---

## Configuration

See **[docs/Server-configuration.md](docs/Server-configuration.md)** for all config options.

Key sections in `config.json`:

```json
{
  "Server": { "PublicIp": "1.2.3.4", "PublicPort": 22023 },
  "Admin":  { "Password": "changeme", "MarketplaceUrl": "https://raw.githubusercontent.com/..." },
  "Auth":   { "EnableIpAuth": false }
}
```

---

## Troubleshooting

See **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)**.

---

## Contributing

See **[CONTRIBUTING.md](CONTRIBUTING.md)**.

---

## Credits

- [Next.Hazel](https://github.com/willardf/Hazel-Networking)
- [Reactor.Impostor](https://github.com/NuclearPowered/Reactor.Impostor)
- [Fast-Impostor / Next-Impostor](https://github.com/BunchHanpiDev/Fast-Impostor)
- [Impostor](https://github.com/Impostor/Impostor) — the original project this is based on

---

## License

Distributed under the **GNU GPLv3** License. See [LICENSE](LICENSE) for details.

### Privacy Policy Notice

The admin panel at `your.domain.com/admin` includes a privacy policy text. As a server operator, you are responsible for making this policy visible to all players before they join. If you have questions, contact us on Discord.
