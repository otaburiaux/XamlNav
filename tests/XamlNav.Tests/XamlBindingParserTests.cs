using System.Linq;
using Xunit;

namespace XamlNav.Tests
{
    public class XamlBindingParserTests
    {
        // -------------------------------------------------------------------
        //  ParseBindings — binding expression recognition
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("{Binding MyProperty}", "MyProperty")]                // Implicit path
        [InlineData("{Binding Path=MyProperty}", "MyProperty")]           // Explicit Path=
        [InlineData("{x:Bind MyProperty}", "MyProperty")]                 // UWP x:Bind
        [InlineData("{Binding Items.MyProperty}", "Items.MyProperty")]    // Dotted path
        [InlineData("{Binding Path=A.B.C}", "A.B.C")]                    // Deep dotted path
        public void ParseBindings_SingleBinding_ReturnsCorrectPath(string bindingExpr, string expectedPath)
        {
            string xaml = $"<TextBlock Text=\"{bindingExpr}\" />";
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();

            Assert.Single(bindings);
            Assert.Equal(expectedPath, bindings[0].PropertyName);
        }

        [Fact]
        public void ParseBindings_MultipleBindings_ReturnsAll()
        {
            string xaml = "<StackPanel><TextBlock Text=\"{Binding Prop1}\" /><TextBlock Text=\"{x:Bind Prop2}\" /></StackPanel>";
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();

            Assert.Equal(2, bindings.Count);
            Assert.Equal("Prop1", bindings[0].PropertyName);
            Assert.Equal("Prop2", bindings[1].PropertyName);
        }

        // -------------------------------------------------------------------
        //  ParseBindings — edge cases & no-match scenarios
        // -------------------------------------------------------------------

        [Theory]
        [InlineData(null)]        // Null input
        [InlineData("")]          // Empty string
        [InlineData("{Binding}")] // No path at all
        [InlineData("<TextBlock Text=\"Plain\" />")] // No binding syntax
        public void ParseBindings_NoValidBindings_ReturnsEmpty(string xaml)
        {
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();
            Assert.Empty(bindings);
        }

        [Fact]
        public void ParseBindings_ConverterOnly_MatchesConverterAsPath()
        {
            // When no explicit Path=, the first identifier after {Binding is treated as the path.
            // "Converter" is technically matched — this documents the current (known) behavior.
            string xaml = "<TextBlock Text=\"{Binding Converter={StaticResource MyConv}}\" />";
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();

            Assert.Single(bindings);
            Assert.Equal("Converter", bindings[0].PropertyName);
        }

        [Fact]
        public void ParseBindings_ExplicitPathWithConverter_ReturnsPathOnly()
        {
            string xaml = "<TextBlock Text=\"{Binding Path=Name, Converter={StaticResource MyConv}}\" />";
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();

            Assert.Single(bindings);
            Assert.Equal("Name", bindings[0].PropertyName);
        }

        // -------------------------------------------------------------------
        //  ParseBindings — offset accuracy (critical for rename)
        // -------------------------------------------------------------------

        [Fact]
        public void ParseBindings_StartPositionAndLength_PointToExactSubstring()
        {
            string xaml = "<TextBlock Text=\"{Binding MyProperty}\" />";
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();

            Assert.Single(bindings);
            var b = bindings[0];

            Assert.Equal("MyProperty", xaml.Substring(b.StartPosition, b.Length));
            Assert.Equal(b.PropertyName.Length, b.Length);
        }

        [Fact]
        public void ParseBindings_BindingExpressionStart_PointsToOpeningBrace()
        {
            string xaml = "<TextBlock Text=\"{Binding MyProperty}\" />";
            var bindings = XamlBindingParser.ParseBindings(xaml).ToList();

            Assert.Single(bindings);
            Assert.Equal('{', xaml[bindings[0].BindingExpressionStart]);
        }

        // -------------------------------------------------------------------
        //  ReplaceSegmentInPath
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("MyProp", "MyProp", "NewProp", "NewProp")]                  // Exact match
        [InlineData("Items.MyProp", "MyProp", "NewProp", "Items.NewProp")]      // Tail segment
        [InlineData("MyProp.Sub", "MyProp", "NewProp", "NewProp.Sub")]          // Head segment
        [InlineData("Root.MyProp.Sub", "MyProp", "NewProp", "Root.NewProp.Sub")]// Middle segment
        [InlineData("OtherProp", "MyProp", "NewProp", "OtherProp")]            // No match
        [InlineData("MyPropExtra", "MyProp", "NewProp", "MyPropExtra")]        // Partial name — no match
        [InlineData("MyProp.MyProp", "MyProp", "X", "X.X")]                    // Multiple occurrences
        public void ReplaceSegmentInPath_ReturnsExpected(string path, string oldName, string newName, string expected)
        {
            string result = XamlBindingParser.ReplaceSegmentInPath(path, oldName, newName);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("", "MyProp", "New", "")]            // Empty path
        [InlineData("MyProp", "", "New", "MyProp")]      // Empty old name
        [InlineData("MyProp", "MyProp", "", "MyProp")]   // Empty new name
        [InlineData(null, "MyProp", "New", null)]        // Null path
        public void ReplaceSegmentInPath_InvalidInputs_ReturnsOriginal(string path, string oldName, string newName, string expected)
        {
            string result = XamlBindingParser.ReplaceSegmentInPath(path, oldName, newName);
            Assert.Equal(expected, result);
        }
    }
}
