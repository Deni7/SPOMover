using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Spo;
using SPOMover.Spo.Graph;
using SPOMover.Spo.Throttling;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>GUI-17: browsing SharePoint while editing a profile — sites, libraries, folders — through a stored connection.</summary>
public sealed class SpoBrowser(RunService runs, ISpoAppFactory factory)
{
    private readonly ConcurrentDictionary<string, SpoApp> _apps = new(StringComparer.OrdinalIgnoreCase);

    private SpoApp App(string connection) => _apps.GetOrAdd(connection, name =>
    {
        var c = runs.Connections().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(L.T($"Connection \"{name}\" not found."));
        // Browsing is light: a small share of the budget so it never competes with a run.
        var tenant = new RuBudget("browse", c.Limits().TenantPer5Min * 0.05, TimeSpan.FromMinutes(5), 0);
        return factory.Create(c, new SpoAppSettings(2, 3, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(15), 0.1), tenant);
    });

    /// <summary>Call after a connection was edited so the next request uses the new settings.</summary>
    public void Forget(string connection) => _apps.TryRemove(connection, out _);

    public Task<IReadOnlyList<SpoSite>> SearchSitesAsync(string connection, string query, CancellationToken ct) =>
        App(connection).Client.SearchSitesAsync(query, ct);

    public async Task<(SpoSite Site, IReadOnlyList<SpoDrive> Drives)> SiteAsync(string connection, string siteUrl, CancellationToken ct)
    {
        var client = App(connection).Client;
        var site = await client.GetSiteAsync(siteUrl, ct).ConfigureAwait(false);
        return (site, await client.GetDrivesAsync(site.Id, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Before a move: the target libraries of a profile against its source (columns, content types, settings),
    /// through the profile's first connection.
    /// </summary>
    public Task<TargetCheckReport> CheckTargetsAsync(Profile profile, CancellationToken ct)
    {
        string connection = profile.Connections.FirstOrDefault() ?? throw new InvalidOperationException(L.T("No connection is selected in the profile."));
        return TargetCheck.RunAsync(App(connection).Client, profile, DateTimeOffset.UtcNow, ct);
    }

    public Task<IReadOnlyList<SpoItem>> FoldersAsync(string connection, string driveId, string? folderId, CancellationToken ct) =>
        App(connection).Client.GetChildFoldersAsync(driveId, folderId, ct);
}

/// <summary>GUI-26: certificates in the Windows stores visible to this process, for picking one by thumbprint.</summary>
public static class CertificateCatalog
{
    /// <param name="HasPrivateKey">The store links a private key to the certificate (a .cer imported into a store has none).</param>
    /// <param name="KeyAccessible">This account can open that key (a LocalMachine key needs Read for the account).</param>
    public sealed record Entry(string Store, string Thumbprint, string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, bool Expired,
        bool HasPrivateKey, bool KeyAccessible = true)
    {
        public bool NotYetValid => NotBefore > DateTime.Now;
        public bool Usable => HasPrivateKey && KeyAccessible && !Expired && !NotYetValid;

        /// <summary>Subject, or the thumbprint when the subject is empty.</summary>
        public string Title => string.IsNullOrWhiteSpace(Subject) ? Thumbprint : Subject;
    }

    /// <summary>The account whose CurrentUser store is listed — the one the app runs under.</summary>
    public static string Account => $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>All certificates, usable ones first; those without a private key are kept so the UI can explain why they cannot be picked.</summary>
    public static IReadOnlyList<Entry> List()
    {
        var list = new List<Entry>();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                foreach (var cert in store.Certificates)
                {
                    using (cert)
                    {
                        list.Add(new Entry(location.ToString(), cert.Thumbprint, cert.Subject, cert.Issuer, cert.NotBefore, cert.NotAfter,
                            cert.NotAfter < DateTime.Now, cert.HasPrivateKey, cert.HasPrivateKey && CanOpenKey(cert)));
                    }
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // No access to the machine store under this account.
            }
        }
        return list.OrderByDescending(e => e.Usable).ThenByDescending(e => e.HasPrivateKey).ThenByDescending(e => e.NotAfter).ToList();
    }

    /// <summary>HasPrivateKey only says the store knows of a key; opening it shows whether this account may use it.</summary>
    private static bool CanOpenKey(X509Certificate2 cert)
    {
        try
        {
            using var key = (System.Security.Cryptography.AsymmetricAlgorithm?)cert.GetRSAPrivateKey() ?? cert.GetECDsaPrivateKey();
            return key is not null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>Estimates from the last run of the profile that listed its libraries.</summary>
public static class ProfileInsights
{
    /// <param name="ByLibrary">Files and bytes by library name (case-insensitive), as the last inventory saw them.</param>
    public sealed record Estimate(long RunId, DateTimeOffset At, IReadOnlyDictionary<string, (long Files, long Bytes)> ByLibrary)
    {
        public (long Files, long Bytes)? Of(string? library) =>
            library is not null && ByLibrary.TryGetValue(library.Trim(), out var v) ? v : null;

        /// <summary>The source of the profile (the first library of the inventory).</summary>
        public long SourceFiles { get; init; }

        public long SourceBytes { get; init; }
    }

    public static Estimate? For(StateStore store, Profile profile)
    {
        if (profile.Name is null || store.LastListingRun(profile.Name) is not { } run)
            return null;
        var names = ReportExporter.LibraryNames(run);
        var byLibrary = new Dictionary<string, (long Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long sourceFiles = 0, sourceBytes = 0;
        foreach (var b in store.BreakdownBy(run.Id, "target_idx"))
        {
            if (!int.TryParse(b.Key, System.Globalization.CultureInfo.InvariantCulture, out int i) || i < 0 || i >= names.Count)
                continue;
            byLibrary[names[i].Trim()] = (b.Total, b.Bytes);
            if (i == 0)
                (sourceFiles, sourceBytes) = (b.Total, b.Bytes);
        }
        foreach (string name in names.Where(n => !byLibrary.ContainsKey(n.Trim())))
            byLibrary[name.Trim()] = (0, 0);
        return new Estimate(run.Id, run.CreatedAt, byLibrary) { SourceFiles = sourceFiles, SourceBytes = sourceBytes };
    }
}
