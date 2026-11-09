using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using SPOMover.Core.Config;
using SPOMover.Core;
using SPOMover.Core.Localization;

namespace SPOMover.Spo.Auth;

/// <summary>Access tokens for Graph or SharePoint. MSAL caches them and renews before they expire (RSL-1).</summary>
public interface ITokenProvider
{
    /// <param name="forceRefresh">After a 401: skip the cache and get a new token.</param>
    Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct);
}

public static class TokenProviderFactory
{
    public static ITokenProvider Create(Connection connection, ILogger logger) => connection.AuthMode switch
    {
        "deviceCode" => new DeviceCodeTokenProvider(connection, logger),
        _ => new CertificateTokenProvider(connection),
    };

    /// <summary>
    /// Tokens for SharePoint REST of one tenant host (https://contoso.sharepoint.com/.default), needed for what Graph
    /// does not offer: check-outs (INV-3) and, from stage 4, moving between libraries. App-only only.
    /// </summary>
    public static Func<string, ITokenProvider> SharePoint(Connection connection)
    {
        var byHost = new System.Collections.Concurrent.ConcurrentDictionary<string, ITokenProvider>(StringComparer.OrdinalIgnoreCase);
        return host => byHost.GetOrAdd(host, h => connection.AuthMode == "certificate"
            ? new CertificateTokenProvider(connection, $"https://{h}/.default")
            : new UnavailableTokenProvider(L.T("SharePoint REST works only with certificate sign-in (authMode = certificate).")));
    }
}

/// <summary>A provider that always refuses, with the reason (no SharePoint tokens with a delegated sign-in).</summary>
public sealed class UnavailableTokenProvider(string reason) : ITokenProvider
{
    public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct) => throw new SpoException(ErrorClass.Auth, reason);
}

/// <summary>SEC-2: app-only with a certificate from the Windows store (CFG-6: only the thumbprint is configured).</summary>
public sealed class CertificateTokenProvider : ITokenProvider
{
    private readonly string[] _scopes;
    private readonly IConfidentialClientApplication _app;

    /// <param name="scope">Resource of the token: Graph by default, or https://tenant.sharepoint.com/.default.</param>
    public CertificateTokenProvider(Connection connection, string scope = "https://graph.microsoft.com/.default")
    {
        _scopes = [scope];
        var certificate = FindCertificate(connection.CertificateThumbprint!, connection.CertificateStore);
        _app = ConfidentialClientApplicationBuilder.Create(connection.ClientId)
            .WithCertificate(certificate, sendX5C: true)
            .WithAuthority(AzureCloudInstance.AzurePublic, connection.TenantId)
            .Build();
    }

    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        try
        {
            var result = await _app.AcquireTokenForClient(_scopes).WithForceRefresh(forceRefresh).ExecuteAsync(ct).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalServiceException ex) when (ex.StatusCode is 400 or 401 or 403)
        {
            throw new SpoException(ErrorClass.Auth, L.T($"Could not get a token: {ex.ErrorCode} {ex.Message}"), inner: ex);
        }
        catch (MsalException ex)
        {
            throw new SpoException(ErrorClass.Transient, L.T($"Could not get a token: {ex.ErrorCode} {ex.Message}"), inner: ex);
        }
    }

    public static X509Certificate2 FindCertificate(string thumbprint, string storeName)
    {
        var location = storeName == "LocalMachine" ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", string.Empty), validOnly: false);
        var cert = found.Count > 0 ? found[0] : null;
        if (cert is null)
            throw new SpoException(ErrorClass.Auth, L.T($"Certificate {thumbprint} not found in store {storeName}\\My."));
        if (!cert.HasPrivateKey)
            throw new SpoException(ErrorClass.Auth, L.T($"Certificate {thumbprint} in store {storeName}\\My has no private key (only the .cer was imported). Choose the store where the certificate was created, or create a new one (help, 1.5, step 1)."));
        try
        {
            using var key = cert.GetRSAPrivateKey();
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new SpoException(ErrorClass.Auth,
                L.T($"No access to the private key of certificate {thumbprint} in {storeName}\\My for {Environment.UserDomainName}\\{Environment.UserName}: {ex.Message} Administrator: certlm.msc → certificate → Manage Private Keys → Read permission for this account."), inner: ex);
        }
        if (cert.NotAfter < DateTime.Now)
            throw new SpoException(ErrorClass.Auth, L.T($"Certificate {thumbprint} has expired ({cert.NotAfter:d})."));
        return cert;
    }
}

/// <summary>SEC-2 fallback: delegated sign-in with a device code; the code is written to the host log.</summary>
public sealed class DeviceCodeTokenProvider(Connection connection, ILogger logger) : ITokenProvider
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/Sites.ReadWrite.All"];
    private readonly IPublicClientApplication _app = PublicClientApplicationBuilder.Create(connection.ClientId)
        .WithAuthority(AzureCloudInstance.AzurePublic, connection.TenantId)
        .WithDefaultRedirectUri()
        .Build();
    private readonly SemaphoreSlim _interactive = new(1, 1);

    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var silent = await _app.AcquireTokenSilent(Scopes, account).WithForceRefresh(forceRefresh).ExecuteAsync(ct).ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
            }
        }

        await _interactive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _app.AcquireTokenWithDeviceCode(Scopes, code =>
            {
                logger.LogWarning("Sign-in for connection {Connection}: {Message}", connection.Name, code.Message);
                return Task.CompletedTask;
            }).ExecuteAsync(ct).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            throw new SpoException(ErrorClass.Auth, L.T($"Device code sign-in failed: {ex.ErrorCode} {ex.Message}"), inner: ex);
        }
        finally
        {
            _interactive.Release();
        }
    }
}
