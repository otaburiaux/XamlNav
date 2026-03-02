using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace XamlNav.Tests
{
    public class XamlDataContextResolverTests : IDisposable
    {
        private readonly XamlDataContextResolver _resolver = new XamlDataContextResolver();
        private readonly List<string> _tempDirs = new List<string>();

        /// <summary>
        /// Creates a temporary directory with XAML and code-behind files for testing.
        /// All temp dirs are cleaned up in Dispose.
        /// </summary>
        private string CreateTestFiles(string xamlFileName, string xamlContent, string csContent)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"XamlResolverTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);

            File.WriteAllText(Path.Combine(dir, xamlFileName), xamlContent);
            if (csContent != null)
                File.WriteAllText(Path.Combine(dir, xamlFileName + ".cs"), csContent);

            return dir;
        }

        public void Dispose()
        {
            foreach (var dir in _tempDirs)
            {
                try { Directory.Delete(dir, true); }
                catch { /* ignore cleanup errors */ }
            }
        }

        // ===================================================================
        //  xmlns Mapping
        // ===================================================================

        [Theory]
        [InlineData("clr-namespace:MyApp.ViewModels;assembly=MyApp", "MyApp.ViewModels")]
        [InlineData("clr-namespace:MyApp.ViewModels", "MyApp.ViewModels")]
        [InlineData("using:MyApp.ViewModels", "MyApp.ViewModels")]
        public void ParseXmlnsMappings_ParsesCorrectly(string xmlns, string expectedNamespace)
        {
            var xaml = $"<Window xmlns:vm=\"{xmlns}\" />";
            var mappings = XamlDataContextResolver.ParseXmlnsMappings(xaml);

            Assert.True(mappings.ContainsKey("vm"));
            Assert.Equal(expectedNamespace, mappings["vm"]);
        }

        [Fact]
        public void ParseXmlnsMappings_MultiplePrefixes_ParsesAll()
        {
            var xaml = @"<Window xmlns:vm=""clr-namespace:MyApp.ViewModels"" xmlns:conv=""clr-namespace:MyApp.Converters;assembly=MyApp"" />";
            var mappings = XamlDataContextResolver.ParseXmlnsMappings(xaml);

            Assert.Equal(2, mappings.Count);
            Assert.Equal("MyApp.ViewModels", mappings["vm"]);
            Assert.Equal("MyApp.Converters", mappings["conv"]);
        }

        // ===================================================================
        //  ResolveTypeName
        // ===================================================================

        [Fact]
        public void ResolveTypeName_PrefixedType_ResolvesFullName()
        {
            var mappings = new Dictionary<string, string> { { "vm", "MyApp.ViewModels" } };
            var result = XamlDataContextResolver.ResolveTypeName("vm:MainViewModel", mappings);

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.MainViewModel", result.FullTypeName);
            Assert.Equal("vm", result.Prefix);
            Assert.Equal("MainViewModel", result.LocalName);
        }

        [Fact]
        public void ResolveTypeName_UnknownPrefix_ReturnsNullFullName()
        {
            var mappings = new Dictionary<string, string> { { "vm", "MyApp.ViewModels" } };
            var result = XamlDataContextResolver.ResolveTypeName("local:SomeType", mappings);

            Assert.NotNull(result);
            Assert.Null(result.FullTypeName);
            Assert.Equal("local", result.Prefix);
            Assert.Equal("SomeType", result.LocalName);
        }

        [Fact]
        public void ResolveTypeName_NoPrefixReturnsNull()
        {
            var mappings = new Dictionary<string, string> { { "vm", "MyApp.ViewModels" } };
            Assert.Null(XamlDataContextResolver.ResolveTypeName("MainViewModel", mappings));
        }

        // ===================================================================
        //  Pattern 1: d:DesignInstance
        // ===================================================================

        [Fact]
        public void Resolve_DesignInstance_WithTypeEquals()
        {
            var xaml = @"<Window
    xmlns:vm=""clr-namespace:MyApp.ViewModels""
    xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
    d:DataContext=""{d:DesignInstance Type=vm:MainViewModel}"">
    <TextBlock Text=""{Binding Title}"" />
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Title}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.MainViewModel", result.FullTypeName);
            Assert.Equal("MainViewModel", result.LocalName);
        }

        [Fact]
        public void Resolve_DesignInstance_WithoutTypeEquals()
        {
            var xaml = @"<Window
    xmlns:vm=""clr-namespace:MyApp.ViewModels""
    xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
    d:DataContext=""{d:DesignInstance vm:OrderViewModel}"">
    <TextBlock Text=""{Binding OrderId}"" />
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding OrderId}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.OrderViewModel", result.FullTypeName);
        }

        [Fact]
        public void Resolve_DesignInstance_WithXType()
        {
            var xaml = @"<Window
    xmlns:local=""clr-namespace:MyApp.ViewModels""
    xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
    d:DataContext=""{d:DesignInstance Type={x:Type local:Class3}}"">
    <TextBlock Text=""{Binding Name}"" />
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Name}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.Class3", result.FullTypeName);
            Assert.Equal("Class3", result.LocalName);
        }

        // ===================================================================
        //  Pattern 1b: Direct DataContext type reference
        // ===================================================================

        [Fact]
        public void Resolve_DirectDataContext_PrefixedType()
        {
            var xaml = @"<Window
    xmlns:vm=""clr-namespace:MyApp.ViewModels""
    DataContext=""{vm:MainViewModel}"">
    <TextBlock Text=""{Binding Title}"" />
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Title}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.MainViewModel", result.FullTypeName);
        }

        [Fact]
        public void Resolve_DirectDataContext_DesignTimePrefix()
        {
            var xaml = @"<Window
    xmlns:vm=""clr-namespace:MyApp.ViewModels""
    xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
    d:DataContext=""{vm:OrderViewModel}"">
    <TextBlock Text=""{Binding Total}"" />
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Total}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.OrderViewModel", result.FullTypeName);
        }

        [Fact]
        public void Resolve_DirectDataContext_IgnoresBindingExtension()
        {
            var xaml = @"<Window
    DataContext=""{Binding Main, Source={StaticResource Locator}}"">
    <TextBlock Text=""{Binding Title}"" />
</Window>";

            // {Binding ...} is NOT a direct type — should not match pattern 1b
            Assert.Null(_resolver.Resolve(xaml, xaml.IndexOf("{Binding Title}")));
        }

        // ===================================================================
        //  Pattern 2: Inline <T.DataContext>
        // ===================================================================

        [Fact]
        public void Resolve_InlineDataContext()
        {
            var xaml = @"<Window xmlns:vm=""clr-namespace:MyApp.ViewModels"">
    <Window.DataContext>
        <vm:MainViewModel />
    </Window.DataContext>
    <TextBlock Text=""{Binding Name}"" />
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Name}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.MainViewModel", result.FullTypeName);
        }

        // ===================================================================
        //  Pattern 3: DataTemplate DataType="{x:Type ...}"
        // ===================================================================

        [Fact]
        public void Resolve_DataTemplate_XType()
        {
            var xaml = @"<Window xmlns:vm=""clr-namespace:MyApp.ViewModels"">
    <DataTemplate DataType=""{x:Type vm:CustomerViewModel}"">
        <TextBlock Text=""{Binding CustomerName}"" />
    </DataTemplate>
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding CustomerName}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.CustomerViewModel", result.FullTypeName);
        }

        // ===================================================================
        //  Pattern 4: x:DataType
        // ===================================================================

        [Fact]
        public void Resolve_XDataType()
        {
            var xaml = @"<Page xmlns:vm=""using:MyApp.ViewModels"">
    <DataTemplate x:DataType=""vm:PersonViewModel"">
        <TextBlock Text=""{Binding FirstName}"" />
    </DataTemplate>
</Page>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding FirstName}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.PersonViewModel", result.FullTypeName);
        }

        // ===================================================================
        //  Priority / Nesting
        // ===================================================================

        [Fact]
        public void Resolve_DataTemplate_TakesPriorityOverDesignInstance()
        {
            var xaml = @"<Window
    xmlns:vm=""clr-namespace:MyApp.ViewModels""
    xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
    d:DataContext=""{d:DesignInstance Type=vm:MainViewModel}"">
    <DataTemplate DataType=""{x:Type vm:ItemViewModel}"">
        <TextBlock Text=""{Binding ItemName}"" />
    </DataTemplate>
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding ItemName}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.ItemViewModel", result.FullTypeName);
        }

        [Fact]
        public void Resolve_BindingOutsideDataTemplate_UsesDesignInstance()
        {
            var xaml = @"<Window
    xmlns:vm=""clr-namespace:MyApp.ViewModels""
    xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
    d:DataContext=""{d:DesignInstance Type=vm:MainViewModel}"">
    <TextBlock Text=""{Binding Title}"" />
    <DataTemplate DataType=""{x:Type vm:ItemViewModel}"">
        <TextBlock Text=""{Binding ItemName}"" />
    </DataTemplate>
</Window>";

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Title}"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.MainViewModel", result.FullTypeName);
        }

        // ===================================================================
        //  No DataContext (null returns)
        // ===================================================================

        [Fact]
        public void Resolve_NoDataContext_ReturnsNull()
        {
            var xaml = @"<Window>
    <TextBlock Text=""{Binding SomeProp}"" />
</Window>";

            Assert.Null(_resolver.Resolve(xaml, xaml.IndexOf("{Binding SomeProp}")));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(999)]
        public void Resolve_InvalidPosition_ReturnsNull(int position)
        {
            Assert.Null(_resolver.Resolve("<Window />", position));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Resolve_EmptyOrNullContent_ReturnsNull(string xaml)
        {
            Assert.Null(_resolver.Resolve(xaml, 0));
        }

        // ===================================================================
        //  Pattern 5: Code-Behind DataContext
        // ===================================================================

        [Fact]
        public void TryResolveFromCodeBehind_FindsConstructorAssignment()
        {
            var dir = CreateTestFiles("MainWindow.xaml", "<Window />", @"
namespace MyApp
{
    public partial class MainWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}");

            var result = XamlDataContextResolver.TryResolveFromCodeBehind(Path.Combine(dir, "MainWindow.xaml"));

            Assert.NotNull(result);
            Assert.Equal("MainViewModel", result.FullTypeName);
            Assert.Equal("MainViewModel", result.LocalName);
        }

        [Fact]
        public void TryResolveFromCodeBehind_FullyQualifiedType()
        {
            var dir = CreateTestFiles("MainWindow.xaml", "<Window />",
                "DataContext = new MyApp.ViewModels.MainViewModel();");

            var result = XamlDataContextResolver.TryResolveFromCodeBehind(Path.Combine(dir, "MainWindow.xaml"));

            Assert.NotNull(result);
            Assert.Equal("MyApp.ViewModels.MainViewModel", result.FullTypeName);
            Assert.Equal("MainViewModel", result.LocalName);
        }

        [Fact]
        public void TryResolveFromCodeBehind_NoCodeBehind_ReturnsNull()
        {
            Assert.Null(XamlDataContextResolver.TryResolveFromCodeBehind(@"C:\nonexistent\Foo.xaml"));
        }

        // ===================================================================
        //  Naming Convention Fallback
        // ===================================================================

        [Theory]
        [InlineData("MainView.xaml", "MainViewModel")]
        [InlineData("MainWindow.xaml", "MainViewModel")]
        [InlineData("OrderPage.xaml", "OrderViewModel")]
        [InlineData("SettingsUserControl.xaml", "SettingsViewModel")]
        [InlineData("ConfirmDialog.xaml", "ConfirmViewModel")]
        [InlineData("Dashboard.xaml", "DashboardViewModel")]
        public void TryResolveFromNamingConvention_ResolvesCorrectly(string fileName, string expectedVmName)
        {
            var result = XamlDataContextResolver.TryResolveFromNamingConvention(Path.Combine(@"C:\project", fileName));

            Assert.NotNull(result);
            Assert.Equal(expectedVmName, result.FullTypeName);
            Assert.Equal(expectedVmName, result.LocalName);
        }

        // ===================================================================
        //  Full Resolve with file path fallback
        // ===================================================================

        [Fact]
        public void Resolve_FallsBackToCodeBehind_WhenNoXamlPattern()
        {
            string xaml = @"<UserControl>
    <TextBlock Text=""{Binding Title}"" />
</UserControl>";

            var dir = CreateTestFiles("MainView.xaml", xaml, @"
namespace MyApp.Views
{
    public partial class MainView
    {
        public MainView()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}");

            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Title}"), Path.Combine(dir, "MainView.xaml"));

            Assert.NotNull(result);
            Assert.Equal("MainViewModel", result.FullTypeName);
        }

        [Fact]
        public void Resolve_FallsBackToNamingConvention_WhenNoOtherPatterns()
        {
            string xaml = @"<UserControl>
    <TextBlock Text=""{Binding Title}"" />
</UserControl>";

            // Non-existent path (no code-behind) with a View suffix
            var result = _resolver.Resolve(xaml, xaml.IndexOf("{Binding Title}"), @"C:\nonexistent\OrderView.xaml");

            Assert.NotNull(result);
            Assert.Equal("OrderViewModel", result.FullTypeName);
        }
    }
}
