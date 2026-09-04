namespace StorageHub.Desktop.Tests;

public sealed class PaneItemNameRulesTests
{
    [Theory]
    [InlineData("Folder")]
    [InlineData("report.txt")]
    [InlineData("Résumé 2026")]
    public void PortableNamesAreAccepted(string name) => Assert.Null(PaneItemNameRules.Validate(name));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("bad/name")]
    [InlineData("trailing.")]
    [InlineData(" leading")]
    [InlineData("NUL.txt")]
    public void UnsafeOrReservedNamesAreRejected(string name) => Assert.NotNull(PaneItemNameRules.Validate(name));
}
