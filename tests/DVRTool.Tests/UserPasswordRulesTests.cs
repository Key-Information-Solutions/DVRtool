using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

public class UserPasswordRulesTests
{
    [Fact]
    public void AcceptsAPasswordThatMeetsTheFloor()
    {
        Assert.Empty(UserPasswordRules.Check("Sunflower9", "jordan"));
    }

    [Theory]
    [InlineData("Ab1")]
    [InlineData("Abc123!")]
    public void RejectsAPasswordShorterThanTheMinimum(string password)
    {
        Assert.Contains(UserPasswordRules.Check(password, "jordan"),
            c => c.Contains("at least"));
    }

    [Fact]
    public void RejectsAPasswordLongerThanTheMaximum()
    {
        Assert.Contains(UserPasswordRules.Check(new string('a', 17) + "A1", "jordan"),
            c => c.Contains("at most"));
    }

    [Theory]
    [InlineData("sunflowers")]
    [InlineData("SUNFLOWERS")]
    [InlineData("1234567890")]
    public void RejectsASingleCharacterClass(string password)
    {
        Assert.Contains(UserPasswordRules.Check(password, "jordan"),
            c => c.Contains("at least two of"));
    }

    [Theory]
    [InlineData("Sunflower")]  // upper + lower
    [InlineData("sunflow3r")]  // lower + digit
    [InlineData("sunflower!")] // lower + symbol
    public void AcceptsAnyTwoCharacterClasses(string password)
    {
        Assert.Empty(UserPasswordRules.Check(password, "jordan"));
    }

    [Fact]
    public void RejectsTheAccountName()
    {
        Assert.Contains(UserPasswordRules.Check("Operator1", "Operator1"),
            c => c.Contains("not be the account name."));
    }

    [Fact]
    public void RejectsTheAccountNameWhateverTheCase()
    {
        Assert.Contains(UserPasswordRules.Check("operator1", "Operator1"),
            c => c.Contains("not be the account name."));
    }

    [Fact]
    public void RejectsTheAccountNameReversed()
    {
        Assert.Contains(UserPasswordRules.Check("1rotarepO", "Operator1"),
            c => c.Contains("reversed"));
    }

    [Fact]
    public void ReportsEveryComplaintAtOnce()
    {
        // Short and single-class: the operator should see both, not fix one and be told the other.
        var complaints = UserPasswordRules.Check("abc", "jordan");
        Assert.Equal(2, complaints.Count);
    }

    [Fact]
    public void IgnoresABlankAccountNameRatherThanMatchingOnIt()
    {
        Assert.Empty(UserPasswordRules.Check("Sunflower9", "   "));
    }
}
