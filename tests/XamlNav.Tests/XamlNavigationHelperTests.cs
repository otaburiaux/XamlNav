using Xunit;

namespace XamlNav.Tests
{
    public class XamlNavigationHelperTests
    {
        // -------------------------------------------------------------------
        //  IsInsideXClass
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("x:Class=\"MyNamespace.MyClass\"", 10, true)]                // Inside value
        [InlineData("x:Class=\"MyNamespace.MyClass\"", 0, false)]                // Before attribute
        [InlineData("x:Class=\"MyNamespace.MyClass\"", 30, false)]               // After value
        [InlineData("x:Name=\"MyClass\"", 10, false)]                            // Different attribute
        [InlineData("  x:Class  =  \"MyNamespace.MyClass\"  ", 20, true)]        // Whitespace around =
        public void IsInsideXClass_ReturnsExpected(string text, int index, bool expected)
        {
            Assert.Equal(expected, XamlNavigationHelper.IsInsideXClass(text, index));
        }

        // -------------------------------------------------------------------
        //  IsInsideSourceAttribute
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("Source=\"Dictionary1.xaml\"", 10, true)]                     // Inside value
        [InlineData("Source=\"Dictionary1.xaml\"", 0, false)]                     // Before attribute
        [InlineData("ItemsSource=\"{Binding Items}\"", 15, false)]               // Prefixed — ignore
        [InlineData("ImageSource=\"icon.png\"", 15, false)]                      // Prefixed — ignore
        [InlineData("DataSource=\"items\"", 12, false)]                          // Prefixed — ignore
        [InlineData("  Source  =  \"Dictionary1.xaml\"  ", 15, true)]            // Whitespace around =
        public void IsInsideSourceAttribute_ReturnsExpected(string text, int index, bool expected)
        {
            Assert.Equal(expected, XamlNavigationHelper.IsInsideSourceAttribute(text, index));
        }

        // -------------------------------------------------------------------
        //  IsInsideEventHandler
        // -------------------------------------------------------------------

        [Theory]
        // Positive: standard WPF events
        [InlineData("<Button Click=\"OnClick\">", 17, true)]
        [InlineData("<Button MouseDoubleClick=\"Button_Click\">", 28, true)]
        [InlineData("<Grid Loaded=\"OnLoaded\">", 15, true)]
        [InlineData("<TextBox PreviewKeyDown=\"OnPreviewKeyDown\">", 30, true)]
        [InlineData("<ListView SelectionChanged=\"OnSelectionChanged\">", 35, true)]
        [InlineData("<Slider ValueChanged=\"OnChanged\">", 24, true)]
        [InlineData("<Grid RequestBringIntoView=\"FrameworkElement_OnRequestBringIntoView\" />", 35, true)]
        // Negative: cursor on tag name, not handler
        [InlineData("<Button Click=\"OnClick\">", 3, false)]
        // Negative: non-event attributes
        [InlineData("<TextBox x:Name=\"MyBox\">", 18, false)]
        [InlineData("<Button Content=\"Click Me\">", 19, false)]
        [InlineData("<Button x:Name=\"MyBtn\">", 18, false)]
        [InlineData("<Button Name=\"MyBtn\">", 16, false)]
        [InlineData("<StackPanel Orientation=\"Horizontal\">", 30, false)]
        [InlineData("<Grid Visibility=\"Collapsed\">", 20, false)]
        [InlineData("<Button Height=\"20\">", 17, false)]
        public void IsInsideEventHandler_ReturnsExpected(string text, int index, bool expected)
        {
            Assert.Equal(expected, XamlNavigationHelper.IsInsideEventHandler(text, index));
        }

        // -------------------------------------------------------------------
        //  IsInsideTagName
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("<local:MyControl/>", 3, true)]              // Prefix part
        [InlineData("<local:MyControl/>", 9, true)]              // Name after colon
        [InlineData("</local:MyControl>", 10, true)]             // Closing tag
        [InlineData("<Grid>", 2, true)]                           // Simple tag
        [InlineData("<Button Content=\"Click Me\"/>", 3, true)]   // Tag with attributes
        [InlineData("  <Button/>", 5, true)]                      // Leading whitespace
        [InlineData("<Grid Column=\"0\">", 12, false)]            // Inside attribute value
        [InlineData("<local:UserControl1 Content=\"Hi\"/>", 25, false)] // Inside attribute value
        [InlineData("some text no tags", 5, false)]               // No tag
        public void IsInsideTagName_ReturnsExpected(string text, int index, bool expected)
        {
            Assert.Equal(expected, XamlNavigationHelper.IsInsideTagName(text, index));
        }

        // -------------------------------------------------------------------
        //  IsLikeEventAttribute — heuristic classification
        // -------------------------------------------------------------------

        [Theory]
        // Prefix-based events
        [InlineData("Click", true)]
        [InlineData("MouseDown", true)]
        [InlineData("MouseDoubleClick", true)]
        [InlineData("KeyUp", true)]
        [InlineData("KeyDown", true)]
        [InlineData("PreviewMouseDown", true)]
        [InlineData("TouchDown", true)]
        [InlineData("DragEnter", true)]
        [InlineData("Drop", true)]
        [InlineData("GestureCompleted", true)]
        // Suffix-based events
        [InlineData("Loaded", true)]
        [InlineData("Unloaded", true)]
        [InlineData("SelectionChanged", true)]
        [InlineData("TextChanged", true)]
        [InlineData("SizeChanged", true)]
        [InlineData("Initialized", true)]
        [InlineData("Closed", true)]
        [InlineData("Opened", true)]
        [InlineData("Completed", true)]
        [InlineData("RequestBringIntoView", true)]
        // Exact-match events
        [InlineData("GotFocus", true)]
        [InlineData("LostFocus", true)]
        // Non-events
        [InlineData("Name", false)]
        [InlineData("Width", false)]
        [InlineData("Height", false)]
        [InlineData("Content", false)]
        [InlineData("Visibility", false)]
        [InlineData("HorizontalAlignment", false)]
        [InlineData("Margin", false)]
        [InlineData("DataContext", false)]
        [InlineData("Text", false)]
        [InlineData("ItemsSource", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsLikeEventAttribute_ReturnsExpected(string name, bool expected)
        {
            Assert.Equal(expected, XamlNavigationHelper.IsLikeEventAttribute(name));
        }

        // -------------------------------------------------------------------
        //  IsNavigablePosition — composite integration tests
        // -------------------------------------------------------------------

        [Theory]
        // Markup extensions
        [InlineData("Text=\"{Binding Path=Name}\"", 15, true)]
        [InlineData("Text=\"{Binding Path=Name}\"", 4, false)]
        [InlineData("Style=\"{StaticResource MyStaticStyle}\"", 15, true)]
        [InlineData("Style=\"{DynamicResource MyDynamicStyle}\"", 15, true)]
        [InlineData("DataType=\"{x:Type viewModels:MainViewModel}\"", 20, true)]
        [InlineData("Text=\"{Binding Path=Name, Converter={StaticResource MyConverter}}\"", 45, true)]
        // x:Static
        [InlineData("<SolidColorBrush x:Key=\"{x:Static SystemColors.HighlightTextBrushKey}\" Color=\"Red\"/>", 30, true)]
        [InlineData("<SolidColorBrush x:Key=\"{x:Static SystemColors.HighlightTextBrushKey}\" Color=\"Red\"/>", 45, true)]
        // Custom markup extensions
        [InlineData("<TextBlock Text=\"{loc:Trad MyText}\"", 20, true)]
        [InlineData("<Grid Visibility=\"{Binding IsVisible, Converter={MyConv:BoolToVisibilityConverter}}\"", 55, true)]
        // Special attributes
        [InlineData("ResourceDictionary Source=\"Styles.xaml\"", 30, true)]
        [InlineData("x:Class=\"MyApp.MainWindow\"", 15, true)]
        // Tag names
        [InlineData("<local:UserControl1/>", 3, true)]
        [InlineData("<local:UserControl1/>", 10, true)]
        [InlineData("<myUserC:UserControl1 MyProp=\"{Binding MyPr}\">", 3, true)]
        [InlineData("</local:UserControl1>", 10, true)]
        // Events
        [InlineData("<Button Click=\"OnCLick\">", 18, true)]
        // Negative
        [InlineData("Text=\"Plain String\"", 5, false)]
        [InlineData("Width=\"100\"", 7, false)]
        [InlineData("<TextBlock Text=\"Hello\">", 18, false)]
        public void IsNavigablePosition_ReturnsExpected(string text, int index, bool expected)
        {
            // Null serviceProvider is safe — the logic handles it gracefully
            Assert.Equal(expected, XamlNavigationHelper.IsNavigablePosition(text, index));
        }

        // -------------------------------------------------------------------
        //  GetWordBoundaries
        // -------------------------------------------------------------------

        [Theory]
        // Dotted paths
        [InlineData("Item.Date", 0, "Item")]     // Start of first segment
        [InlineData("Item.Date", 2, "Item")]     // Middle of first segment
        [InlineData("Item.Date", 4, "Item")]     // On dot — picks left word
        [InlineData("Item.Date", 5, "Date")]     // After dot
        // Prefixed names
        [InlineData("myConv:Converters", 3, "myConv")]      // In prefix
        [InlineData("myConv:Converters", 6, "myConv")]      // On colon — picks prefix
        [InlineData("myConv:Converters", 7, "Converters")]  // After colon
        // General identifiers
        [InlineData("  SomeWord  ", 4, "SomeWord")]
        [InlineData("word_with_underscore", 5, "word_with_underscore")]
        public void GetWordBoundaries_ReturnsExpectedWord(string text, int index, string expectedWord)
        {
            bool success = XamlNavigationHelper.GetWordBoundaries(text, index, out int start, out int end);

            Assert.True(success);
            Assert.Equal(expectedWord, text.Substring(start, end - start));
        }

        [Theory]
        [InlineData("", 0)]            // Empty string
        [InlineData("   ", 1)]         // Only whitespace
        [InlineData("abc", 5)]         // Index beyond text length
        [InlineData("abc", -1)]        // Negative index
        public void GetWordBoundaries_InvalidInput_ReturnsFalse(string text, int index)
        {
            bool success = XamlNavigationHelper.GetWordBoundaries(text, index, out _, out _);
            Assert.False(success);
        }

        // -------------------------------------------------------------------
        //  ShouldSkipFile
        // -------------------------------------------------------------------

        [Theory]
        // Should skip: build/temp directories
        [InlineData(@"C:\project\bin\Debug\file.xaml", true)]
        [InlineData(@"C:\project\obj\Release\file.xaml", true)]
        [InlineData(@"C:\project\.vs\cache\file.xaml", true)]
        [InlineData(@"C:\project\.git\hooks\file.xaml", true)]
        [InlineData("C:/project/bin/Debug/file.xaml", true)]     // Forward slashes
        [InlineData("C:/project/obj/Release/file.xaml", true)]
        // Should not skip: legitimate source directories
        [InlineData(@"C:\project\src\Views\file.xaml", false)]
        [InlineData(@"C:\project\src\MyControl.xaml", false)]
        [InlineData(@"C:\project\binder\file.xaml", false)]      // "bin" substring ≠ \bin\
        [InlineData(@"C:\project\objects\file.xaml", false)]     // "obj" substring ≠ \obj\
        public void ShouldSkipFile_ReturnsExpected(string path, bool expected)
        {
            Assert.Equal(expected, XamlNavigationHelper.ShouldSkipFile(path));
        }
    }
}
