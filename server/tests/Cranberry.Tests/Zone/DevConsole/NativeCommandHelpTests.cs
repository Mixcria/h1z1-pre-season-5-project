using Cranberry.Zone.DevConsole;

namespace Cranberry.Tests.Zone.DevConsole;

public sealed class NativeCommandHelpTests
{
    [Fact]
    public void AllHarvestedCommandsAreDiscoverableWithoutIncludingCvars()
    {
        var displayed = new HashSet<string>();
        int rows = HelpFormatter.PageSize - 2;
        for (int page = 1; page <= (NativeCommandHelp.Commands.Count + rows - 1) / rows; page++)
        {
            var lines = HelpFormatter.Render(TestCatalog.Build(), CommandLine.Parse("commands", $"native {page}"));
            Assert.InRange(lines.Count, 3, HelpFormatter.PageSize);
            foreach (string line in lines.Skip(2)) Assert.True(displayed.Add(line.Split(' ')[0][1..]));
        }
        Assert.Equal(ClientRegistry1148.Entries.Where(e => e.Kind != "cvar-static").Select(e => e.Name).Order(),
            displayed.Order());
        Assert.DoesNotContain("vehicle_process_driven_only", displayed);
    }

    [Fact]
    public void NativeVehicleHelpTeachesOriginalNumericSyntaxAndGroundPlacement()
    {
        var lines = HelpFormatter.Render(TestCatalog.Build(), CommandLine.Parse("commands", "native vehicle"));
        Assert.Contains(lines, l => l.StartsWith("/vehicle <id>"));
        Assert.Contains(lines, l => l.Contains("1 OffRoader; 2 PickupTruck; 3 PoliceCar; 5 ATV."));
        Assert.Contains(lines, l => l.Contains("client supplies the placement"));
        Assert.DoesNotContain("vehicle", TestCatalog.Build().Names);
    }

    [Fact]
    public void NativeLookupDoesNotClaimCustomCommandsAreOriginal()
    {
        Assert.StartsWith("- no original August command", NativeCommandHelp.Render("car")[0]);
        Assert.Contains(NativeCommandHelp.Render("god"), l => l.Contains("Original alias: gm invuln"));
    }
}
