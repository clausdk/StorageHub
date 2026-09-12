namespace StorageHub.Desktop.Tests;

/// <summary>
/// The catalog is the single source of truth the export dialog and the import wizard both read,
/// so a mistake here shows up as the two offering different things.
/// </summary>
public sealed class SettingsSectionCatalogTests
{
    [Fact]
    public void EverySectionIsDescribedOnceWithAUniqueKey()
    {
        var sections = SettingsSectionCatalog.Sections;

        Assert.Equal(Enum.GetValues<SettingsSectionId>().Length, sections.Count);
        Assert.Equal(sections.Count, sections.Select(section => section.Id).Distinct().Count());
        Assert.Equal(
            sections.Count,
            sections.Select(section => section.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            sections.Count,
            sections.Select(section => section.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.All(sections, section =>
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Key));
            Assert.False(string.IsNullOrWhiteSpace(section.Label));
            Assert.False(string.IsNullOrWhiteSpace(section.Description));
            Assert.Equal(section, SettingsSectionCatalog.Get(section.Id));
        });
    }

    [Fact]
    public void OnlyMachineSpecificStartsUnticked()
    {
        // The one place the "off by default" decision lives; both dialogs seed from it.
        Assert.DoesNotContain(SettingsSectionId.MachineSpecific, SettingsSectionCatalog.Defaults);
        Assert.Equal(
            SettingsSectionCatalog.Sections.Count - 1,
            SettingsSectionCatalog.Defaults.Count);
    }

    [Fact]
    public void AgentBackedSectionsAreExactlyTheOnesTheAgentOwns()
    {
        var agentBacked = SettingsSectionCatalog.Sections
            .Where(section => section.RequiresAgent)
            .Select(section => section.Id)
            .ToArray();

        Assert.Equal(
            [SettingsSectionId.Connections, SettingsSectionId.SyncProfiles, SettingsSectionId.Schedules],
            agentBacked);
    }

    [Fact]
    public void DependenciesNameRealSectionsAndNeverThemselves()
    {
        Assert.All(SettingsSectionCatalog.Sections, section => Assert.All(section.DependsOn, dependency =>
        {
            Assert.NotEqual(section.Id, dependency);
            Assert.NotNull(SettingsSectionCatalog.Get(dependency));
        }));
    }

    [Fact]
    public void TheDependencyGraphIsAcyclic()
    {
        // Both ExpandForExport and ApplyOrder would loop or mis-order on a cycle.
        foreach (var section in SettingsSectionCatalog.Sections)
        {
            var seen = new HashSet<SettingsSectionId>();
            var pending = new Stack<SettingsSectionId>(section.DependsOn);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                Assert.NotEqual(section.Id, current);
                if (!seen.Add(current)) continue;
                foreach (var next in SettingsSectionCatalog.Get(current).DependsOn)
                {
                    pending.Push(next);
                }
            }
        }
    }

    [Fact]
    public void ExportingSchedulesPullsInWhatTheyCannotStandWithout()
    {
        var expanded = SettingsSectionCatalog.ExpandForExport([SettingsSectionId.Schedules]);

        Assert.Contains(SettingsSectionId.Schedules, expanded);
        Assert.Contains(SettingsSectionId.SyncProfiles, expanded);
        Assert.Contains(SettingsSectionId.Connections, expanded);
        Assert.DoesNotContain(SettingsSectionId.MachineSpecific, expanded);
    }

    [Fact]
    public void ExpandingLeavesIndependentSectionsAlone()
    {
        var expanded = SettingsSectionCatalog.ExpandForExport(
            [SettingsSectionId.Shortcuts, SettingsSectionId.DesktopGeneral]);

        Assert.Equal(
            [SettingsSectionId.DesktopGeneral, SettingsSectionId.Shortcuts],
            expanded.OrderBy(id => (int)id));
        Assert.Empty(SettingsSectionCatalog.ExpandForExport([]));
    }

    [Fact]
    public void UntickingConnectionsDropsWhateverDependedOnThem()
    {
        var collapsed = SettingsSectionCatalog.CollapseForExport(
            [SettingsSectionId.Schedules, SettingsSectionId.SyncProfiles, SettingsSectionId.Shortcuts]);

        Assert.Equal([SettingsSectionId.Shortcuts], collapsed);
    }

    [Fact]
    public void ApplyOrderPutsEveryDependencyBeforeWhatNeedsIt()
    {
        var order = SettingsSectionCatalog.ApplyOrder;

        Assert.Equal(SettingsSectionCatalog.Sections.Count, order.Count);
        foreach (var section in order)
        {
            var position = order.ToList().FindIndex(candidate => candidate.Id == section.Id);
            foreach (var dependency in section.DependsOn)
            {
                var dependencyPosition = order.ToList().FindIndex(candidate => candidate.Id == dependency);
                Assert.True(
                    dependencyPosition < position,
                    $"{dependency} must be applied before {section.Id}.");
            }
        }
    }
}
