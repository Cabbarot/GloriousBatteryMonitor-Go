using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace GBM.Desktop.Controls;

/// <summary>
/// Compact secondary-battery readout (Guardian / Dock). Shows a labelled
/// percentage and a slim fill bar coloured by charge level, and dims to an
/// em dash when the cell isn't present.
/// </summary>
public partial class CellChip : UserControl
{
    private TextBlock? _label;
    private TextBlock? _percent;
    private TextBlock? _suffix;
    private Border? _fill;
    private Grid? _barGrid;
    private Ellipse? _dot;

    public CellChip()
    {
        InitializeComponent();
        _label = this.FindControl<TextBlock>("LabelText");
        _percent = this.FindControl<TextBlock>("PercentText");
        _suffix = this.FindControl<TextBlock>("PercentSuffix");
        _fill = this.FindControl<Border>("FillBar");
        _barGrid = this.FindControl<Grid>("BarGrid");
        _dot = this.FindControl<Ellipse>("AccentDot");
        Apply();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<CellChip, string>(nameof(Label), "BATTERY");

    public static readonly StyledProperty<int> LevelProperty =
        AvaloniaProperty.Register<CellChip, int>(nameof(Level));

    public static readonly StyledProperty<bool> PresentProperty =
        AvaloniaProperty.Register<CellChip, bool>(nameof(Present));

    public static readonly StyledProperty<string> AccentProperty =
        AvaloniaProperty.Register<CellChip, string>(nameof(Accent), "#22c55e");

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public int Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool Present
    {
        get => GetValue(PresentProperty);
        set => SetValue(PresentProperty, value);
    }

    public string Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty ||
            change.Property == LevelProperty ||
            change.Property == PresentProperty ||
            change.Property == AccentProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        // All controls must be resolved (they are, after InitializeComponent).
        if (_label is null || _percent is null || _suffix is null ||
            _fill is null || _barGrid is null || _dot is null)
            return;

        _label.Text = Label;

        if (!Present)
        {
            // No pack in this slot: show an em dash, hide the % suffix, empty bar.
            _percent.Text = "\u2014";          // —
            _suffix.IsVisible = false;
            SetBar(0);
            _dot.Fill = MutedBrush();
            _fill.Background = MutedBrush();
            return;
        }

        int level = Level < 0 ? 0 : Level > 100 ? 100 : Level;
        _percent.Text = level.ToString();
        _suffix.IsVisible = true;

        // Colour by charge level, matching the main gauge thresholds.
        var color = level >= 50 ? Color.Parse("#22c55e")
                  : level >= 20 ? Color.Parse("#f59e0b")
                  : Color.Parse("#ef4444");
        var brush = new SolidColorBrush(color);
        _dot.Fill = brush;
        _fill.Background = brush;
        SetBar(level);
    }

    /// <summary>
    /// Sets the fill width by encoding the percentage into the bar Grid's two
    /// star columns (fill : rest). Avalonia lays this out proportionally with no
    /// manual pixel math and no dependence on measured bounds, so it is correct
    /// on first render and on every resize.
    /// </summary>
    private void SetBar(int level)
    {
        if (_barGrid is null) return;
        int fill = level < 0 ? 0 : level > 100 ? 100 : level;
        _barGrid.ColumnDefinitions = new ColumnDefinitions($"{fill}*,{100 - fill}*");
    }

    private IBrush MutedBrush() =>
        this.FindResource("TextMutedBrush") as IBrush
        ?? new SolidColorBrush(Color.Parse("#6b7280"));
}
