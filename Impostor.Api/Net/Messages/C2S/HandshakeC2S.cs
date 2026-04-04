using Impostor.Api.Innersloth;

namespace Impostor.Api.Net.Messages.C2S
{
    /// <summary>
    /// 解析客户端 UDP 游戏握手包（InnerNetClient.GetConnectionData，非 DTLS 路径）。
    ///
    /// 格式（Among Us 客户端源码 InnerNetClient.GetConnectionData，非 DTLS 分支）：
    ///   GameVersion  (int32,  4B)
    ///   Name         (string)
    ///   [V1+] LastNonce / Nonce (uint32, 4B)  ← 服务端通过 DTLS 颁发的 Nonce，认证时使用
    ///   [V2+] Language (uint32) + ChatMode (byte)
    ///   [V3+] PlatformData (message) + CrossplayFlags (int32)
    ///   [V4+] unknown byte (hardcoded 0)
    ///
    /// Nonce 说明：
    ///   - Among Us 客户端先连 DTLS port+2，服务端回复一个 uint32 Nonce。
    ///   - 客户端随后连 UDP port（游戏端口），握手包中 LastNonce 字段即为该 Nonce。
    ///   - 服务端以 Nonce 为 key 查找对应 FriendCode，实现精确的一对一匹配。
    /// </summary>
    public static class HandshakeC2S
    {
        public static void Deserialize(
            IMessageReader reader,
            out GameVersion clientVersion,
            out string name,
            out Language language,
            out QuickChatModes chatMode,
            out PlatformSpecificData? platformSpecificData,
            out uint nonce)
        {
            clientVersion = reader.ReadGameVersion();
            name = reader.ReadString();

            // V1+: LastNonce（服务端颁发的 Nonce，用于 FriendCode 匹配）
            nonce = 0;
            if (clientVersion >= Version.V1)
            {
                nonce = reader.ReadUInt32();
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
                reader.ReadInt32(); // crossplayFlags, not used yet
            }
            else
            {
                platformSpecificData = null;
            }

            if (clientVersion >= Version.V4)
            {
                reader.ReadByte(); // purpose unknown, hardcoded 0 by client
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
