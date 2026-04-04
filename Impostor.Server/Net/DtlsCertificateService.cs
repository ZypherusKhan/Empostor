using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Impostor.Api.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Impostor.Server.Net
{
    internal sealed class DtlsCertificateService
    {
        private readonly ILogger<DtlsCertificateService> _logger;
        private readonly AuthConfig _config;
        private X509Certificate2? _certificate;

        private const string AutoCertFile = "dtls_cert.pem";
        private const string AutoKeyFile  = "dtls_key.pem";

        public DtlsCertificateService(
            ILogger<DtlsCertificateService> logger,
            IOptions<AuthConfig> config)
        {
            _logger = logger;
            _config = config.Value;
        }

        public X509Certificate2 GetOrCreateCertificate()
        {
            if (_certificate != null)
            {
                return _certificate;
            }

            if (_config.EnableNonceAuth
                && !string.IsNullOrWhiteSpace(_config.DtlsCertFile)
                && !string.IsNullOrWhiteSpace(_config.DtlsKeyFile))
            {
                if (File.Exists(_config.DtlsCertFile) && File.Exists(_config.DtlsKeyFile))
                {
                    try
                    {
                        _certificate = LoadFromFiles(_config.DtlsCertFile, _config.DtlsKeyFile);
                        _logger.LogInformation(
                            "[DTLS] Loaded certificate from {Cert} + {Key}",
                            _config.DtlsCertFile, _config.DtlsKeyFile);
                        return _certificate;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[DTLS] Failed to load certificate from {Cert} + {Key}, falling back to auto-generate",
                            _config.DtlsCertFile, _config.DtlsKeyFile);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[DTLS] Certificate files not found ({Cert} / {Key}), falling back to auto-generate",
                        _config.DtlsCertFile, _config.DtlsKeyFile);
                }
            }

            if (File.Exists(AutoCertFile) && File.Exists(AutoKeyFile))
            {
                try
                {
                    var loaded = LoadFromFiles(AutoCertFile, AutoKeyFile);
                    if (loaded.NotAfter > DateTime.UtcNow.AddDays(7))
                    {
                        _certificate = loaded;
                        _logger.LogInformation("[DTLS] Loaded existing self-signed certificate from disk");
                        return _certificate;
                    }

                    _logger.LogInformation("[DTLS] Existing certificate expires soon, regenerating");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DTLS] Failed to load existing certificate, regenerating");
                }
            }

            _certificate = GenerateSelfSigned();
            SaveToDisk(_certificate, AutoCertFile, AutoKeyFile);
            _logger.LogInformation(
                "[DTLS] Generated new self-signed certificate (saved to {Cert} + {Key})",
                AutoCertFile, AutoKeyFile);

            return _certificate;
        }

        private static X509Certificate2 LoadFromFiles(string certFile, string keyFile)
        {
            var certPem = File.ReadAllText(certFile);
            var keyPem  = File.ReadAllText(keyFile);
            var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);
            var cert = X509Certificate2.CreateFromPem(certPem);
            return cert.CopyWithPrivateKey(rsa);
        }

        private static X509Certificate2 GenerateSelfSigned()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=Impostor DTLS Auth",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));

            var cert = req.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(2));

            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
        }

        private void SaveToDisk(X509Certificate2 cert, string certFile, string keyFile)
        {
            try
            {
                File.WriteAllText(certFile, cert.ExportCertificatePem());
                using var rsa = cert.GetRSAPrivateKey()!;
                File.WriteAllText(keyFile, rsa.ExportRSAPrivateKeyPem());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DTLS] Failed to save certificate to disk (non-fatal)");
            }
        }
    }
}
