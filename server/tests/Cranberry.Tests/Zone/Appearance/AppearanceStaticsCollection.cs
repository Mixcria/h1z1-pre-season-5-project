namespace Cranberry.Tests.Zone.Appearance;

/// <summary>
/// The xUnit collection every test that <b>reads or writes</b> one of the two process-wide
/// appearance switches must belong to:
/// <c>AugustDynamicAppearanceTable.ApplyAppearanceRowOverrides</c>,
/// <c>AugustDynamicAppearanceTable.ApplyAuthoredRows</c> (docs/106 addendum 2026-09-03),
/// <c>AugustDynamicAppearanceTable.ShipWholeTable</c> (D316, docs/124) and
/// <c>AugustWornVisuals.SendShaderParameterGroup</c>.
/// <para>
/// <b>Why it exists (wave-5 verify pass).</b> Both switches are mutable statics. xUnit runs test
/// CLASSES in parallel — one collection per class by default, and this tree has no
/// <c>xunit.runner.json</c> and no <c>[CollectionBehavior]</c> — so a sentinel in one class can
/// observe the value another class is holding inside a <c>try</c>/<c>finally</c>. That made
/// <c>Wave5IntegrationTests.TheWornColourExperimentStaysOff</c> — the test whose entire job is
/// "regression guard 1 is not asked to move" — <b>flaky by construction</b>: it asserts
/// <c>SendShaderParameterGroup</c> is false while
/// <c>AugustWornVisualsTests.TheShaderGroupIsAvailableButOptIn</c> sets it true.
/// </para>
/// <para>
/// The pre-existing <c>ApplyAppearanceRowOverrides</c> pair was safe only by accident of placement
/// (its reader and its writer happen to live in one class, and xUnit serialises within a class).
/// Naming the hazard rather than relying on that is the point: a guard that fails at random is a
/// guard the next wave learns to re-run rather than to believe.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class AppearanceStaticsCollection
{
    /// <summary>The collection name. Use <c>[Collection(AppearanceStaticsCollection.Name)]</c>.</summary>
    public const string Name = "appearance-statics";
}
