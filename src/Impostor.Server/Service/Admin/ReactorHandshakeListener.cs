using System.Collections.Generic;
using Impostor.Api.Events;
using Impostor.Api.Events.Client;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.Service.Admin
{
    // From Reactor.Impostor
    internal sealed class ReactorHandshakeListener : IEventListener
    {
        // "reactor" in little-endian ASCII occupies the first 7 bytes after game version
        // Reactor appends its magic to the end of the normal Hazel handshake data:
        // [normal AU handshake] [uint32 magic=0x17c70011] [byte reactorVersion] [packed int modCount]
        // [for each mod: string id, string version, ushort flags]
        private const uint ReactorMagic = 0x17c70011;

        private readonly ILogger<ReactorHandshakeListener> _logger;

        public ReactorHandshakeListener(ILogger<ReactorHandshakeListener> logger)
            => _logger = logger;

        [EventListener]
        public void OnClientConnection(IClientConnectionEvent e)
        {
            var reader = e.HandshakeData;

            // Save position so we can seek to the Reactor section
            // Normal AU handshake: GameVersion(4) Name(string) LastNonce(4) Language(4) ChatMode(1) PlatformData(msg) ...
            // Reactor appends after all that. We need to scan for the magic.
            // Simplest: try to read from current position after AU handshake was already consumed upstream.
            // IClientConnectionEvent fires BEFORE the handshake is processed, so we get the raw bytes.
            // Skip the standard AU fields first.

            var savedPos = reader.Position;
            try
            {
                ReadReactor(e, reader);
            }
            catch
            {
                // Not a Reactor client or parse error — silently skip
            }
            finally
            {
                // Restore position so the rest of the pipeline still works
                reader.Seek(savedPos);
            }
        }

        private void ReadReactor(IClientConnectionEvent e, IMessageReader reader)
        {
            // Skip GameVersion (4 bytes)
            reader.ReadInt32();

            // Skip Name (string = length-prefixed)
            reader.ReadString();

            // Skip LastNonce (4 bytes)
            if (reader.Position + 4 > reader.Length) return;
            reader.ReadUInt32();

            // Skip Language (4) + ChatMode (1)
            if (reader.Position + 5 > reader.Length) return;
            reader.ReadUInt32();
            reader.ReadByte();

            // Skip PlatformData message (sub-message)
            if (reader.Position >= reader.Length) return;
            using var _ = reader.ReadMessage();

            // Skip extra uint32 (crossplay flags)
            if (reader.Position + 4 > reader.Length) return;
            reader.ReadUInt32();

            // Now check for Reactor magic uint32
            if (reader.Position + 4 > reader.Length) return;
            var magic = reader.ReadUInt32();
            if (magic != ReactorMagic) return;

            // Reactor protocol version
            var protocolVersion = reader.ReadByte();

            // Mod count (packed int)
            var modCount = reader.ReadPackedInt32();
            var mods = new ClientMod[modCount];

            for (var i = 0; i < modCount; i++)
            {
                var id = reader.ReadString();
                var version = reader.ReadString();
                var flags = reader.ReadUInt16();
                var requiredOnAll = (flags & 0x01) != 0;
                mods[i] = new ClientMod(id, version, requiredOnAll);
            }

            var info = new ReactorModInfo($"v{protocolVersion}", mods);
            e.Connection.SetReactorMods(info);

            _logger.LogInformation(
                "[ReactorMods] {Name} has {Count} mod(s): {Mods}",
                e.Connection.Client?.Name ?? "unknown",
                mods.Length,
                string.Join(", ", System.Linq.Enumerable.Select(mods, m => $"{m.Id} {m.Version}")));
        }
    }

    public sealed class ReactorModInfo
    {
        public ReactorModInfo(string protocolVersion, IReadOnlyList<ClientMod> mods)
        {
            ProtocolVersion = protocolVersion;
            Mods = mods;
        }

        public string ProtocolVersion { get; }

        public IReadOnlyList<ClientMod> Mods { get; }
    }

    public sealed class ClientMod
    {
        public ClientMod(string id, string version, bool requiredOnAllClients)
        {
            Id = id;
            Version = version;
            RequiredOnAllClients = requiredOnAllClients;
        }

        public string Id { get; }

        public string Version { get; }

        public bool RequiredOnAllClients { get; }
    }
}
