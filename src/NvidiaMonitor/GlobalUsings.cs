// Project-wide WPF global usings.
//
// WPF (net8.0-windows) does not auto-emit global usings for the WPF
// namespaces, but Theme.cs (which this project must not modify) references
// System.Windows.Controls types (DataGrid, TextBlock, ...) without importing
// them. Declaring them globally here lets every file in this assembly —
// including Theme.cs — resolve those types with no per-file edit.
global using System.Windows;
global using System.Windows.Controls;
global using System.Windows.Media;
global using System.Windows.Controls.Primitives;