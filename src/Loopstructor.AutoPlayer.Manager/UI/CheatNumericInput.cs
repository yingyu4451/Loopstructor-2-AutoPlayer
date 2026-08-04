using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Loopstructor.AutoPlayer.Manager.UI;

internal sealed class CheatNumericInput : UserControl
{
    private readonly TextBox _textBox;
    private decimal _minimum;
    private decimal _maximum = 100;
    private decimal _value;
    private decimal _increment = 1;
    private int _decimalPlaces;
    private bool _updatingText;

    public CheatNumericInput()
    {
        MinHeight = 34;
        Grid layout = new();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });

        _textBox = new TextBox
        {
            Padding = new Thickness(8, 5, 6, 5),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            FontFamily = new FontFamily("Cascadia Mono, Consolas")
        };
        _textBox.LostKeyboardFocus += (_, _) => CommitText();
        _textBox.PreviewKeyDown += TextBoxOnPreviewKeyDown;
        Grid.SetColumn(_textBox, 0);
        layout.Children.Add(_textBox);

        Grid steppers = new();
        steppers.RowDefinitions.Add(new RowDefinition());
        steppers.RowDefinitions.Add(new RowDefinition());
        RepeatButton up = CreateStepper(up: true);
        RepeatButton down = CreateStepper(up: false);
        up.Click += (_, _) => Step(1);
        down.Click += (_, _) => Step(-1);
        Grid.SetRow(up, 0);
        Grid.SetRow(down, 1);
        steppers.Children.Add(up);
        steppers.Children.Add(down);
        Grid.SetColumn(steppers, 1);
        layout.Children.Add(steppers);
        Content = layout;
        UpdateText();
    }

    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < value) _maximum = value;
            Value = _value;
        }
    }

    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            if (_minimum > value) _minimum = value;
            Value = _value;
        }
    }

    public decimal Value
    {
        get => _value;
        set
        {
            decimal rounded = Math.Round(value, _decimalPlaces, MidpointRounding.AwayFromZero);
            decimal next = Math.Clamp(rounded, _minimum, _maximum);
            if (_value == next && !string.IsNullOrEmpty(_textBox.Text)) return;
            _value = next;
            UpdateText();
        }
    }

    public decimal Increment
    {
        get => _increment;
        set => _increment = value <= 0 ? 1 : value;
    }

    public int DecimalPlaces
    {
        get => _decimalPlaces;
        set
        {
            _decimalPlaces = Math.Clamp(value, 0, 8);
            UpdateText();
        }
    }

    public void CommitText()
    {
        if (_updatingText) return;
        string text = _textBox.Text.Trim();
        NumberStyles styles = NumberStyles.Float | NumberStyles.AllowThousands;
        if (!decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out decimal parsed)
            && !decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out parsed))
        {
            UpdateText();
            return;
        }

        Value = parsed;
        UpdateText();
    }

    private static RepeatButton CreateStepper(bool up)
    {
        System.Windows.Shapes.Path arrow = new()
        {
            Width = 7,
            Height = 4,
            Stretch = Stretch.Fill,
            Fill = new SolidColorBrush(Color.FromRgb(240, 178, 61)),
            Data = Geometry.Parse(up ? "M 0 4 L 3.5 0 L 7 4 Z" : "M 0 0 L 3.5 4 L 7 0 Z")
        };
        return new RepeatButton
        {
            Content = arrow,
            Padding = new Thickness(0),
            Delay = 350,
            Interval = 80,
            Background = new SolidColorBrush(Color.FromRgb(58, 37, 23)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(112, 73, 43)),
            BorderThickness = new Thickness(1),
            Focusable = false
        };
    }

    private void TextBoxOnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            CommitText();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Up)
        {
            CommitText();
            Step(1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Down)
        {
            CommitText();
            Step(-1);
            eventArgs.Handled = true;
        }
    }

    private void Step(int direction)
    {
        CommitText();
        Value += direction * _increment;
        _textBox.SelectAll();
    }

    private void UpdateText()
    {
        if (_textBox == null) return;
        _updatingText = true;
        try
        {
            _textBox.Text = _value.ToString("N" + _decimalPlaces, CultureInfo.CurrentCulture);
            _textBox.CaretIndex = _textBox.Text.Length;
        }
        finally
        {
            _updatingText = false;
        }
    }
}
