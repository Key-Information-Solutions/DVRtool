using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DVRTool.Core;

namespace DVRTool.App;

/// <summary>
/// A compact multi-select over saved devices: a toggle button that drops a checkbox list.
/// Used wherever a read fans out over several devices at once — the Users tab's device
/// set, the Access tab's saved panels.
/// </summary>
public partial class DevicePicker : UserControl
{
    /// <summary>One selectable device. Notifies so All/None reach the checkboxes.</summary>
    public sealed class Choice(SavedDevice device, bool selected) : INotifyPropertyChanged
    {
        public SavedDevice Device { get; } = device;
        public string Label => Device.Name.Length > 0 ? Device.Name : Device.Address;

        private bool _isSelected = selected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<Choice> _choices = [];

    public DevicePicker()
    {
        InitializeComponent();
        ChoiceList.ItemsSource = _choices;
        Loaded += (_, _) => UpdateLabel();
    }

    /// <summary>
    /// Where the list comes from. Re-queried every time the dropdown opens, so a device
    /// added, renamed or removed mid-session shows up without any refresh wiring.
    /// </summary>
    public Func<IEnumerable<SavedDevice>>? ChoicesProvider { get; set; }

    /// <summary>Shown inside the dropdown when the provider returns nothing.</summary>
    public string EmptyHint { get; set; } = "No saved devices yet.";

    /// <summary>What the toggle says while nothing is selected.</summary>
    public string Prompt { get; set; } = "Choose devices…";

    public IReadOnlyList<SavedDevice> Selected =>
        _choices.Where(c => c.IsSelected).Select(c => c.Device).ToList();

    /// <summary>
    /// Rebuilds the list from the provider, keeping selections across the rebuild. An
    /// edited record is a new instance, so continuity is by identity-pin address — the
    /// stable name for "the same box" — rather than by reference.
    /// </summary>
    public void Refresh()
    {
        var selected = _choices.Where(c => c.IsSelected)
            .Select(c => c.Device.Address)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _choices.Clear();
        foreach (var device in ChoicesProvider?.Invoke() ?? [])
            _choices.Add(new Choice(device, selected.Contains(device.Address)));

        bool empty = _choices.Count == 0;
        EmptyText.Text = EmptyHint;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BulkButtons.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateLabel();
    }

    private void OnDropdownOpened(object sender, RoutedEventArgs e) => Refresh();

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var choice in _choices)
            choice.IsSelected = selected;
        UpdateLabel();
    }

    private void OnChoiceToggled(object sender, RoutedEventArgs e) => UpdateLabel();

    private void UpdateLabel()
    {
        var picked = _choices.Where(c => c.IsSelected).Select(c => c.Label).ToList();
        Label.Text = picked.Count switch
        {
            0 => Prompt,
            1 => picked[0],
            2 => $"{picked[0]}, {picked[1]}",
            _ => $"{picked[0]} + {picked.Count - 1} more",
        };
    }
}
