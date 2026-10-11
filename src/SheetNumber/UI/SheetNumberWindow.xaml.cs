using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SheetNumber.Models;
using SheetNumber.Services;

namespace SheetNumber.UI;

public partial class SheetNumberWindow : Window
{
    private const int MinimumPrefixCount = 1;
    private const int MaximumPrefixCount = 3;

    private readonly SheetNumberCatalog _catalog;
    private readonly List<PrefixRow> _prefixRows = [];
    private Dictionary<string, string>? _valueMap;

    public SheetNumberRequest? Request { get; private set; }

    public SheetNumberWindow(SheetNumberCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();

        SourceParameterCombo.ItemsSource = catalog.ParameterNames;
        DestinationParameterCombo.ItemsSource = catalog.ParameterNames;
        PrefixTargetCombo.ItemsSource = catalog.ParameterNames;
        ValueMapParameterCombo.ItemsSource = catalog.ParameterNames;

        Select(SourceParameterCombo, name => name.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase));
        Select(DestinationParameterCombo, name => name.Equals("SheetNumber2", StringComparison.OrdinalIgnoreCase));
        Select(PrefixTargetCombo, name => name.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase));
        Select(ValueMapParameterCombo, name => IsDisciplineOriginator(name) && !IsSubDiscipline(name));
        AddPrefixRow(name => IsDisciplineOriginator(name) && !IsSubDiscipline(name));
        AddPrefixRow(IsSubDiscipline);

        SkipPrefixedCheckBox.IsChecked = true;

        SourceParameterCombo.SelectionChanged += (_, _) => RefreshPreviewIfOpen();
        DestinationParameterCombo.SelectionChanged += (_, _) => RefreshPreviewIfOpen();
        PrefixTargetCombo.SelectionChanged += (_, _) => RefreshPreviewIfOpen();
        ValueMapParameterCombo.SelectionChanged += (_, _) =>
        {
            RefreshValueMapList();
            RefreshPreviewIfOpen();
        };
        SkipPrefixedCheckBox.Checked += (_, _) => RefreshPreviewIfOpen();
        SkipPrefixedCheckBox.Unchecked += (_, _) => RefreshPreviewIfOpen();

        ApplyButton.IsEnabled = catalog.SheetCount > 0;
        RefreshValueMapList();
    }

    private void LoadMappingFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "Condensed_Parameter.csv"
        };

        string? folder = FindTitleblockFolder();
        if (folder != null)
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _valueMap = ValueMapFile.Read(dialog.FileName);
            MappingFileText.Text = Path.GetFileName(dialog.FileName);
            RefreshValueMapList();
            RefreshPreviewIfOpen();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Sheet Number", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshValueMapList()
    {
        string? parameterName = ValueMapParameterCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(parameterName)
            || !_catalog.ValuesByParameter.TryGetValue(parameterName, out IReadOnlyList<string>? values))
        {
            ValueMapList.ItemsSource = null;
            return;
        }

        ValueMapList.ItemsSource = values.Select(value => new ValueMapRow
        {
            Current = value,
            Mapped = _valueMap != null && _valueMap.TryGetValue(value, out string? mapped)
                ? mapped
                : ""
        }).ToList();
    }

    private static string? FindTitleblockFolder()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Titileblock"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Titleblock")
        };

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            foreach (string folder in Directory.GetDirectories(profile, "OneDrive*"))
            {
                candidates.Add(Path.Combine(folder, "Desktop", "Titileblock"));
                candidates.Add(Path.Combine(folder, "Desktop", "Titleblock"));
            }
        }
        catch
        {
            // The profile folder may not be readable.
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private void AddPrefix_Click(object sender, RoutedEventArgs e)
    {
        AddPrefixRow(null);
        RefreshPreviewIfOpen();
    }

    private void AddPrefixRow(Func<string, bool>? prefer)
    {
        if (_prefixRows.Count >= MaximumPrefixCount)
            return;

        var combo = new ComboBox
        {
            Height = 28,
            MaxDropDownHeight = 360,
            VerticalAlignment = VerticalAlignment.Center
        };
        combo.ItemsSource = _catalog.ParameterNames;
        combo.SelectionChanged += (_, _) => RefreshPreviewIfOpen();
        if (prefer != null)
            Select(combo, prefer);

        var remove = new Button
        {
            Content = "Remove",
            Width = 76,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };

        var row = new PrefixRow(combo, remove);
        remove.Click += (_, _) => RemovePrefixRow(row);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(combo);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        row.Panel = grid;

        _prefixRows.Add(row);
        PrefixRows.Children.Add(grid);
        UpdatePrefixButtons();
    }

    private void RemovePrefixRow(PrefixRow row)
    {
        if (_prefixRows.Count <= MinimumPrefixCount)
            return;

        _prefixRows.Remove(row);
        PrefixRows.Children.Remove(row.Panel);
        UpdatePrefixButtons();
        RefreshPreviewIfOpen();
    }

    private void UpdatePrefixButtons()
    {
        bool canRemove = _prefixRows.Count > MinimumPrefixCount;
        foreach (PrefixRow row in _prefixRows)
            row.RemoveButton.IsEnabled = canRemove;

        AddPrefixButton.IsEnabled = _prefixRows.Count < MaximumPrefixCount;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSelection(out _, out _, out _, out _))
            return;

        PreviewBorder.Visibility = Visibility.Visible;
        UpdatePreview();
    }

    private void RefreshPreviewIfOpen()
    {
        if (PreviewBorder.Visibility == Visibility.Visible)
            UpdatePreview();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSelection(out string? source, out string? destination, out string? prefixTarget, out IReadOnlyList<string>? prefixes))
            return;

        Request = new SheetNumberRequest
        {
            SourceParameter = source!,
            DestinationParameter = destination!,
            PrefixTargetParameter = prefixTarget!,
            PrefixParameters = prefixes!,
            SkipIfAlreadyPrefixed = SkipPrefixedCheckBox.IsChecked == true,
            ValueMapParameter = _valueMap == null ? null : ValueMapParameterCombo.SelectedItem as string,
            ValueMap = _valueMap
        };

        DialogResult = true;
    }

    private bool TryReadSelection(out string? source, out string? destination, out string? prefixTarget, out IReadOnlyList<string>? prefixes)
    {
        source = SourceParameterCombo.SelectedItem as string;
        destination = DestinationParameterCombo.SelectedItem as string;
        prefixTarget = PrefixTargetCombo.SelectedItem as string;
        var selectedPrefixes = new List<string>();
        foreach (PrefixRow row in _prefixRows)
        {
            if (row.Combo.SelectedItem is string name)
                selectedPrefixes.Add(name);
        }

        prefixes = selectedPrefixes;

        if (source == null || destination == null || prefixTarget == null || selectedPrefixes.Count != _prefixRows.Count)
        {
            MessageBox.Show(this, "Choose a parameter in every list.", "Sheet Number", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Source Parameter and Destination Parameter have to be different.", "Sheet Number", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private void UpdatePreview()
    {
        string? source = SourceParameterCombo.SelectedItem as string;
        string? destination = DestinationParameterCombo.SelectedItem as string;
        string? prefixTarget = PrefixTargetCombo.SelectedItem as string;
        IReadOnlyList<string> prefixes = _prefixRows
            .Select(row => row.Combo.SelectedItem as string)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToList();

        if (string.IsNullOrEmpty(_catalog.PreviewSheetLabel))
        {
            PreviewText.Text = "This project has no sheets to update.";
            return;
        }

        string sourceLabel = source ?? "Source Parameter";
        string destLabel = destination ?? "Destination Parameter";
        string targetLabel = prefixTarget ?? "Parameter";
        bool targetIsDestination = string.Equals(prefixTarget, destination, StringComparison.OrdinalIgnoreCase);
        var lines = new List<string> { _catalog.PreviewSheetLabel };

        if (string.IsNullOrEmpty(source) || !_catalog.PreviewValues.TryGetValue(source, out string? sourceValue))
        {
            string missing = string.IsNullOrEmpty(source) ? "(choose a parameter)" : "(not found on this sheet)";
            lines.Add($"{sourceLabel}  =  {missing}");
            lines.Add($"{destLabel}  =  {missing}");
            PreviewText.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        string targetValue = targetIsDestination ? sourceValue : RawValue(_catalog, prefixTarget);
        if (string.IsNullOrEmpty(targetIsDestination ? sourceValue : targetValue))
        {
            lines.Add($"{targetLabel}  =  (empty)");
            lines.Add("This sheet will be skipped because the parameter is empty.");
            PreviewText.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        string? mappedParameter = _valueMap == null ? null : ValueMapParameterCombo.SelectedItem as string;
        string prefix = string.Concat(prefixes.Select(name =>
            ValueMapFile.MappedValue(_valueMap, name, mappedParameter, RawValue(_catalog, name))));
        string updated = prefix + (targetIsDestination ? sourceValue : targetValue);
        if (targetIsDestination)
        {
            lines.Add($"{sourceLabel}  =  {sourceValue}");
            lines.Add($"{destLabel}  =  {updated}");
        }
        else
        {
            lines.Add($"{destLabel}  =  {sourceValue}");
            lines.Add($"{targetLabel}  =  {updated}");
        }

        foreach (string name in prefixes)
        {
            if (!_catalog.PreviewValues.ContainsKey(name))
                lines.Add($"{name} was not found on this sheet.");
        }

        if (SkipPrefixedCheckBox.IsChecked == true
            && _catalog.PreviewValues.TryGetValue(targetLabel, out string? existingTarget)
            && string.Equals(existingTarget, updated, StringComparison.Ordinal))
        {
            lines.Add("This sheet already has this value, so it will be skipped.");
        }

        PreviewText.Text = string.Join(Environment.NewLine, lines);
    }

    private void Select(ComboBox combo, Func<string, bool> predicate)
    {
        string? match = _catalog.ParameterNames.FirstOrDefault(predicate);
        if (match != null)
            combo.SelectedItem = match;
    }

    private static string RawValue(SheetNumberCatalog catalog, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        return catalog.PreviewValues.TryGetValue(name, out string? value) ? value : "";
    }

    private static bool IsSubDiscipline(string name)
    {
        return name.Contains("Sub-Discipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Sub Discipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Subdiscipline", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisciplineOriginator(string name)
    {
        return name.Contains("Discipline", StringComparison.OrdinalIgnoreCase)
               && (name.Contains("Originator", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Orginator", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ValueMapRow
    {
        public required string Current { get; init; }
        public required string Mapped { get; init; }
    }

    private sealed class PrefixRow(ComboBox combo, Button removeButton)
    {
        public ComboBox Combo { get; } = combo;
        public Button RemoveButton { get; } = removeButton;
        public Grid Panel { get; set; } = new();
    }
}
