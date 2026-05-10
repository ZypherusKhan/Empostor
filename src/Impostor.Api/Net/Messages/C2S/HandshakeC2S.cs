using Impostor.Api.Innersloth;

namespace Impostor.Api.Net.Messages.C2S
{
    public static class HandshakeC2S
    {
        public static void Deserialize(
            IMessageReader reader,
            out GameVersion clientVersion,
            out string name,
            out Language language,
            out QuickChatModes chatMode,
            out PlatformSpecificData? platformSpecificData,
            out string? matchmakerToken)
        {
            clientVersion = reader.ReadGameVersion();
            name = reader.ReadString();

            matchmakerToken = null;
            if (clientVersion >= Version.V1)
            {
                try
                {
                    var token = reader.ReadString();
                    matchmakerToken = string.IsNullOrEmpty(token) ? null : token;
                }
                catch
                {
                }
            }

            if (clientVersion >= Version.V2)
            {
                language = (Language)reader.ReadUInt32();
                chatMode = (QuickChatModes)reader.ReadByte();
            }
            else
            {
                language = Language.English;
                chatMode = QuickChatModes.FreeChatOrQuickChat;
            }

            if (clientVersion >= Version.V3)
            {
                using var platformReader = reader.ReadMessage();
                platformSpecificData = new PlatformSpecificData(platformReader);

                if (reader.Position < reader.Length)
                {
                    try { reader.ReadString(); } catch { /* ignore */ }
                }
            }
            else
            {
                platformSpecificData = null;
            }

            if (clientVersion >= Version.V4)
            {
                try { reader.ReadByte(); } catch { /* ignore */ }
            }
        }

        private static class Version
        {
            public static readonly GameVersion V1 = new(2021, 4, 25);
            public static readonly GameVersion V2 = new(2021, 6, 30);
            public static readonly GameVersion V3 = new(2021, 11, 9);
            public static readonly GameVersion V4 = new(2021, 12, 14);
        }
    }
}
