using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Tests that swap PKHeX's global event tables (EncounterEvent.RefreshMGDB) run one at a time;
/// in parallel, one test's reset could land in the middle of another's check.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GlobalEventDatabaseCollection
{
    public const string Name = "Global event database";
}
