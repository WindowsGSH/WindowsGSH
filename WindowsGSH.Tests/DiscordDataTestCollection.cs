using Xunit;

namespace WindowsGSH.Tests;

/// <summary>
/// Every test class that reads/writes <c>discord_server_settings</c> (or the <c>app_settings</c>
/// marker <see cref="WindowsGSH.Data.DiscordChannelBackfillService"/> uses) shares this
/// non-parallel collection. <c>DiscordChannelBackfillService.Backfill</c> scans every row in
/// <c>discord_server_settings</c>, not just rows a given test created itself - a row left behind
/// by a concurrently-running test in another class was observed making an unrelated test's
/// <c>RanAnyBackfill</c>/<c>BackfilledServerIds</c> assertions flaky.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiscordDataTestCollection
{
    public const string Name = "Discord data";
}
