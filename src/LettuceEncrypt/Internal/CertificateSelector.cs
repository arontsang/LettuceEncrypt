﻿// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using McMaster.AspNetCore.Kestrel.Certificates;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LettuceEncrypt.Internal;

internal class CertificateSelector : IServerCertificateSelector
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _certs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, X509Certificate2> _challengeCerts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptions<LettuceEncryptOptions> _options;
    private readonly ILogger<CertificateSelector> _logger;

    public CertificateSelector(IOptions<LettuceEncryptOptions> options, ILogger<CertificateSelector> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IEnumerable<string> SupportedDomains => _certs.Keys;

    public virtual void Add(X509Certificate2 certificate)
    {
        var preloaded = false;
        foreach (var dnsName in X509CertificateHelpers.GetAllDnsNames(certificate))
        {
            var selectedCert = AddWithDomainName(_certs, dnsName, certificate);

            // Call preload once per certificate, but only if the certificate is actually selected to be used
            // for this domain. This is a small optimization which avoids preloading on a cert that may not be used.
            if (!preloaded && selectedCert == certificate)
            {
                preloaded = true;
                PreloadIntermediateCertificates(selectedCert);
            }
        }
    }

    public void AddChallengeCert(X509Certificate2 certificate)
    {
        foreach (var dnsName in X509CertificateHelpers.GetAllDnsNames(certificate))
        {
            AddWithDomainName(_challengeCerts, dnsName, certificate);
        }
    }

    public void ClearChallengeCert(string domainName)
    {
        _challengeCerts.TryRemove(domainName, out _);
    }

    /// <summary>
    /// Registers the certificate for usage with domain unless there is already a newer cert for this domain.
    /// </summary>
    /// <param name="certs"></param>
    /// <param name="domainName"></param>
    /// <param name="certificate"></param>
    /// <returns>The certificate current selected to be used for this domain</returns>
    private X509Certificate2 AddWithDomainName(ConcurrentDictionary<string, X509Certificate2> certs, string domainName,
        X509Certificate2 certificate)
    {
        return certs.AddOrUpdate(
            domainName,
            certificate,
            (_, currentCert) =>
            {
                if (currentCert == null || certificate.NotAfter >= currentCert.NotAfter)
                {
                    return certificate;
                }

                return currentCert;
            });
    }

    public bool HasCertForDomain(string domainName)
    {
        if (_certs.ContainsKey(domainName))
        {
            return true;
        }
        if (_certs.Keys.Any(n => n.StartsWith("*") && domainName.EndsWith(n[1..])))
        {
            return true;
        }
        return false;
    }

    public X509Certificate2? Select(ConnectionContext context, string? domainName)
    {
        if (_challengeCerts.Count > 0)
        {
            // var sslStream = context.Features.Get<SslStream>();
            // sslStream.NegotiatedApplicationProtocol hasn't been set yet, so we have to assume that
            // if ALPN challenge certs are configured, we must respond with those.

            if (domainName != null && _challengeCerts.TryGetValue(domainName, out var challengeCert))
            {
                _logger.LogTrace("Using ALPN challenge cert for {domainName}", domainName);

                return challengeCert;
            }
        }

        if (domainName == null || !TryGet(domainName, out var retCert))
        {
            return _options.Value.FallbackCertificate;
        }

        return retCert;
    }

    public void Reset(string domainName)
    {
        _certs.TryRemove(domainName, out _);
    }

    public bool TryGet(string domainName, out X509Certificate2? certificate)
    {
        if (_certs.TryGetValue(domainName, out certificate)) return true;
        var wildcardDomainName = _certs.Keys.FirstOrDefault(n => n.StartsWith("*") && domainName.EndsWith(n[1..]));
        if (wildcardDomainName != null && _certs.TryGetValue(wildcardDomainName, out certificate))
        {
            return true;
        }
        return false;
    }

    private void PreloadIntermediateCertificates(X509Certificate2 certificate)
    {
        if (certificate.IsSelfSigned())
        {
            return;
        }

        // workaround for https://github.com/dotnet/aspnetcore/issues/21183
        using var chain = new X509Chain
        {
            ChainPolicy =
            {
                RevocationMode = X509RevocationMode.NoCheck
            }
        };

        var commonName = X509CertificateHelpers.GetCommonName(certificate);
        try
        {
            if (chain.Build(certificate))
            {
                _logger.LogTrace("Successfully tested certificate chain for {commonName}", commonName);
                return;
            }
        }
        catch (CryptographicException ex)
        {
            _logger.LogDebug(ex, "Failed to validate certificate chain for {commonName}", commonName);
        }

        _logger.LogWarning(
            "Failed to validate certificate for {commonName} ({thumbprint}). This could cause an outage of your app.",
            commonName, certificate.Thumbprint);
    }
}
