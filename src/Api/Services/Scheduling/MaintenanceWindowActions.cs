using TheKrystalShip.KGSM.Core.Scheduling;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Services.Scheduling;

/// <summary>
/// The actions a list of maintenance windows runs unattended. The scheduler runs a window as itself,
/// attributed to the person who saved it, so the check that the person may do what it does happens when
/// the windows are saved: their token has to list every action here.
/// </summary>
public static class MaintenanceWindowActions
{
    /// <summary>The actions the windows in <paramref name="packed"/> run, in a stable order.</summary>
    /// <remarks>
    /// A backup takes a backup and prunes the old ones; an update installs it; a restart restarts. A window
    /// that updates or restarts warns the players first, which is announcing to them.
    /// </remarks>
    public static IReadOnlyList<string> Required(string packed)
    {
        HashSet<string> actions = new(StringComparer.Ordinal);

        foreach (MaintenanceWindow window in MaintenanceWindowParser.Parse(packed).Where(w => w.IsValid))
        {
            foreach (MaintenanceTask task in window.Tasks)
            {
                switch (task)
                {
                    case MaintenanceTask.Backup:
                        actions.Add(ActionIds.ServerBackupsCreate);
                        actions.Add(ActionIds.ServerBackupsManage);
                        break;
                    case MaintenanceTask.Update:
                        actions.Add(ActionIds.ServerUpdate);
                        actions.Add(ActionIds.ServerAnnounce);
                        break;
                    case MaintenanceTask.Restart:
                        actions.Add(ActionIds.ServerRestart);
                        actions.Add(ActionIds.ServerAnnounce);
                        break;
                }
            }
        }

        return [.. actions.Order(StringComparer.Ordinal)];
    }
}
