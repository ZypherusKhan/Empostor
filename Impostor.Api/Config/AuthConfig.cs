namespace Impostor.Api.Config
{
    /// <summary>
    /// 认证模式配置。在 config.json 的 "Auth" 节点下配置。
    ///
    /// 三种模式（互斥，EnableNonceAuth 优先级最高）：
    ///
    ///   EnableNonceAuth = true
    ///     DTLS 端口 (port+2) + 从 DtlsCertFile/DtlsKeyFile 加载管理员证书。
    ///     服务端对每个 DTLS 会话颁发唯一 Nonce，发回客户端，
    ///     客户端在 UDP 握手中携带 Nonce，服务端以 Nonce 精确查找 FriendCode。
    ///     一对一，无竞争条件，适合有自定义证书的场景。
    ///
    ///   EnableIpAuth = true  (且 EnableNonceAuth = false)
    ///     DTLS 端口 (port+2) + 自动生成自签名证书（持久化至磁盘）。
    ///     同样使用 Nonce 精确匹配（修复了 NextImpostor 原版 IP 竞争问题）。
    ///     适合不需要客户端信任证书、仅想获取 FriendCode 的私有服务器。
    ///
    ///   两者均为 false（默认）
    ///     跳过 FriendCode 验证，行为与原版 ImpostorFast 完全一致。
    /// </summary>
    public class AuthConfig
    {
        public const string Section = "Auth";

        /// <summary>
        /// 开启 IP 认证模式。
        /// 自动生成自签名证书（dtls_cert.pem + dtls_key.pem），使用 Nonce 匹配 FriendCode。
        /// </summary>
        public bool EnableIpAuth { get; set; } = false;

        /// <summary>
        /// 开启 Nonce 认证模式（从文件加载证书，优先级高于 EnableIpAuth）。
        /// 需配置 DtlsCertFile 和 DtlsKeyFile 指向 PEM 格式的证书和私钥。
        /// </summary>
        public bool EnableNonceAuth { get; set; } = false;

        /// <summary>
        /// PEM 格式证书文件路径（EnableNonceAuth 模式使用）。
        /// </summary>
        public string DtlsCertFile { get; set; } = "dtls_cert.pem";

        /// <summary>
        /// PEM 格式 RSA 私钥文件路径（EnableNonceAuth 模式使用）。
        /// </summary>
        public string DtlsKeyFile { get; set; } = "dtls_key.pem";

        /// <summary>
        /// Nonce 有效期（秒）。客户端需在此时间内完成 UDP 握手，否则 Nonce 过期。
        /// </summary>
        public int NonceTtlSeconds { get; set; } = 30;

        /// <summary>是否启用了任意一种认证模式。</summary>
        public bool IsAuthEnabled => EnableIpAuth || EnableNonceAuth;
    }
}
