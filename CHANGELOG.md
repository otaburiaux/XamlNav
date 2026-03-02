# Changelog

## [1.0.0] - 2026-02-24

### Added

- Initial release
- Ctrl+Click Go To Definition on XAML binding expressions (`{Binding}`, `{x:Bind}`), code-behind classes (e.g. `x:Class="WpfApp.MainWindow"`) , namespace-qualified types (e.g. `Converter={myConv:MyValueConverter}`), UserControl tags, and event handlers
- Nested property path navigation (e.g. `{Binding Items.Count}`)
- Find All References with XAML via context menu, Extensions menu, or Ctrl+Shift+Click in XAML or C#; results shown in the standard Find All References window with theme-aware syntax highlighting
- Rename with XAML via context menu or Extensions menu, with a inline popup, live preview, and a unified C#+XAML undo transaction
- Restart XAML Designer via XAML context menu or Extensions menu
- DataContext-aware binding resolution using six strategies: `DataTemplate.DataType` / `x:DataType`, `d:DesignInstance`, direct type markup extensions, inline property elements, code-behind analysis, and naming conventions
- Configurable modifier+click actions (Go To Definition / Find All References / None) under Tools > Options > XamlNav > General
- Options to include unverified bindings in Find All References results and rename operations
- Dedicated *XamlNav Debug* output pane (View > Output) logging all navigation and resolution steps
