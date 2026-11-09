using SPOMover.Core.Config;
using SPOMover.Core;
using SPOMover.Core.Localization;
using SPOMover.Spo;
using SPOMover.Spo.Graph;

namespace SPOMover.Engine;

public static class TargetResolver
{
    /// <summary>Site → library → folder of a target (GUI-17 by URL: works with Sites.Selected).</summary>
    public static async Task<(SpoDrive Drive, string? RootItem, string RootPath)> ResolveAsync(GraphClient client, Target target, CancellationToken ct)
    {
        var site = await client.GetSiteAsync(target.SiteUrl!, ct).ConfigureAwait(false);
        var drives = await client.GetDrivesAsync(site.Id, ct).ConfigureAwait(false);
        string wanted = target.Library!.Trim().TrimEnd('/');
        var drive = drives.FirstOrDefault(d =>
                string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(d.WebUrl.TrimEnd('/')), Uri.UnescapeDataString(wanted), StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(d.WebUrl.TrimEnd('/').Split('/')[^1]), wanted, StringComparison.OrdinalIgnoreCase));
        if (drive is null)
        {
            string names = string.Join(", ", drives.Select(d => d.Name));
            throw new SpoException(ErrorClass.File, L.T($"library \"{target.Library}\" not found on site {target.SiteUrl} (there are: {names})."));
        }

        string folder = (target.Folder ?? string.Empty).Replace('\\', '/').Trim('/');
        if (folder.Length == 0)
            return (drive, null, string.Empty);
        var item = await client.GetItemByPathAsync(drive.Id, folder, ct).ConfigureAwait(false);
        if (!item.IsFolder)
            throw new SpoException(ErrorClass.File, L.T($"\"{folder}\" in library {drive.Name} is not a folder."));
        return (drive, item.Id, folder);
    }

}
