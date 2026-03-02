using Xunit;

namespace XamlNav.Tests
{
    public class XamlReferenceFinderTests
    {
        // -------------------------------------------------------------------
        //  IsMatch — segment-level matching in dotted binding paths
        // -------------------------------------------------------------------

        [Theory]
        // Exact match
        [InlineData("MyProp", "MyProp", true)]
        // Segment match within dotted paths
        [InlineData("Items.MyProp", "MyProp", true)]       // Tail
        [InlineData("MyProp.Inner", "MyProp", true)]       // Head
        [InlineData("Root.Sub.MyProp", "MyProp", true)]    // Middle
        [InlineData("A.MyProp.B.C", "MyProp", true)]       // Deep path
        // No match — partial name overlap
        [InlineData("OtherProp", "MyProp", false)]
        [InlineData("MyProp2", "MyProp", false)]           // Suffix mismatch
        [InlineData("MyProperty", "MyProp", false)]        // Prefix mismatch
        [InlineData("AMyProp", "MyProp", false)]           // Prefix mismatch without dot
        // Edge cases
        [InlineData("", "MyProp", false)]                  // Empty path
        [InlineData("MyProp", "", false)]                  // Empty target
        [InlineData(null, "MyProp", false)]                // Null path
        [InlineData("MyProp", null, false)]                // Null target
        public void IsMatch_ReturnsExpected(string path, string target, bool expected)
        {
            bool result = XamlReferenceFinder.IsMatch(path, target);
            Assert.Equal(expected, result);
        }
    }
}
