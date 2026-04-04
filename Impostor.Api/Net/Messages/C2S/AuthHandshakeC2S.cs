using Impostor.Api.Innersloth;

namespace Impostor.Api.Net.Messages.C2S
{
    /// <summary>
    /// 解析 Among Us 客户端发往认证端口 (port+2) 的 DTLS 握手包。
    ///
    /// 格式（参照 AuthManager.BuildData）：
    ///   GameVersion  (int32,  4B)
    ///   Platform     (byte,   1B)  — 平台枚举，忽略
    ///   MatchmakerToken (string)   — EOS matchmaker token（自定义服务器通常为空）
    ///   FriendCode   (string)      — 玩家的好友码，格式 "Name#XXXX"
    /// </summary>
    public static class AuthHandshakeC2S
    {
        public static void Deserialize(
            IMessageReader reader,
            out GameVersion clientVersion,
            out string matchmakerToken,
            out string friendCode)
        {
            clientVersion = reader.ReadGameVersion();
            _ = reader.ReadByte();           // Platform enum, unused
            matchmakerToken = reader.ReadString();
            friendCode = reader.ReadString();
        }
    }
}
