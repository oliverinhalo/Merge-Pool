using System.Collections.ObjectModel;
using System.Globalization;

namespace MergePool.Ui.ViewModels.Settings;

/// <summary>
/// One setting, described well enough that nobody has to guess what moving it does.
/// </summary>
/// <remarks>
/// <para>Every setting carries four pieces of prose rather than a bare label: what it is called,
/// what it does, what you get and give up by moving it, and — once it has a value — a sentence
/// saying what that value means in practice. The window renders all four, so a slider is never a
/// nameless bar between two numbers.</para>
/// <para>Settings also know their own default and whether they are sitting on it, which is what
/// lets the window mark the ones you have changed and offer each one its own way back.</para>
/// </remarks>
public abstract class SettingItem : ViewModelBase
{
    private bool _isVisible = true;

    protected SettingItem(string title, string explanation, string effect, bool advanced)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A setting needs a name.", nameof(title));
        }

        // Enforced rather than encouraged: an unexplained setting is the thing this screen exists
        // to avoid, and a missing sentence is easier to catch here than in a screenshot.
        if (string.IsNullOrWhiteSpace(explanation))
        {
            throw new ArgumentException($"'{title}' has no explanation.", nameof(explanation));
        }

        if (string.IsNullOrWhiteSpace(effect))
        {
            throw new ArgumentException($"'{title}' does not say what moving it does.", nameof(effect));
        }

        Title = title;
        Explanation = explanation;
        Effect = effect;
        IsAdvanced = advanced;
        ResetCommand = new RelayCommand(Reset, () => !IsDefault);
    }

    /// <summary>What the setting is called.</summary>
    public string Title { get; }

    /// <summary>What it does, in one or two sentences.</summary>
    public string Explanation { get; }

    /// <summary>What you gain and give up by moving it — the reason to touch it or leave it.</summary>
    public string Effect { get; }

    /// <summary>Whether it only appears once Advanced is on.</summary>
    public bool IsAdvanced { get; }

    /// <summary>What this setting is set to, said in words rather than as a number.</summary>
    public abstract string Summary { get; }

    /// <summary>The value it ships with, for the line that says so.</summary>
    public abstract string DefaultText { get; }

    /// <summary>Whether it is still on that default. The window marks the ones that are not.</summary>
    public abstract bool IsDefault { get; }

    /// <summary>Puts this one setting back to its default, without touching the others.</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>Whether Advanced being off has hidden it.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set => Set(ref _isVisible, value);
    }

    /// <summary>The tooltip: everything about the setting, for the mouse rather than the eye.</summary>
    public string Help => $"{Explanation}\n\n{Effect}\n\nDefault: {DefaultText}";

    public abstract void Reset();

    protected void RaiseValueChanged()
    {
        Raise(nameof(Summary));
        Raise(nameof(IsDefault));
        Raise(nameof(Help));
        ResetCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>A setting with a range: a slider, a live readout, and a sentence about where it is.</summary>
public sealed class NumericSetting : SettingItem
{
    private readonly Func<double, string> _format;
    private readonly Func<double, string> _describe;
    private readonly Action<double>? _changed;
    private double _value;

    public NumericSetting(
        string title,
        string explanation,
        string effect,
        double minimum,
        double maximum,
        double step,
        double defaultValue,
        Func<double, string> format,
        Func<double, string> describe,
        Action<double>? changed = null,
        bool advanced = false)
        : base(title, explanation, effect, advanced)
    {
        if (maximum <= minimum)
        {
            throw new ArgumentException($"'{title}' has an empty range.", nameof(maximum));
        }

        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        DefaultValue = Math.Clamp(defaultValue, minimum, maximum);

        _format = format ?? throw new ArgumentNullException(nameof(format));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _changed = changed;
        _value = DefaultValue;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    /// <summary>The smallest move the slider makes, which is also what counts as "the same value".</summary>
    public double Step { get; }

    public double DefaultValue { get; }

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(double.IsFinite(value) ? value : DefaultValue, Minimum, Maximum);

            if (Math.Abs(clamped - _value) < Step / 2)
            {
                return;
            }

            _value = clamped;
            Raise();
            Raise(nameof(ValueText));
            RaiseValueChanged();
            _changed?.Invoke(clamped);
        }
    }

    /// <summary>The value itself, formatted — "2.0 GB", "45%", "32 MB/s".</summary>
    public string ValueText => _format(_value);

    /// <summary>The ends of the range, labelled, so the slider's travel means something.</summary>
    public string MinimumText => _format(Minimum);

    public string MaximumText => _format(Maximum);

    public override string Summary => _describe(_value);

    public override string DefaultText => _format(DefaultValue);

    public override bool IsDefault => Math.Abs(_value - DefaultValue) < Step / 2;

    /// <summary>
    /// Takes a value from the service without reporting a change, so loading the current settings
    /// does not look like someone editing them.
    /// </summary>
    public void Load(double value)
    {
        _value = Math.Clamp(double.IsFinite(value) ? value : DefaultValue, Minimum, Maximum);
        Raise(nameof(Value));
        Raise(nameof(ValueText));
        RaiseValueChanged();
    }

    public override void Reset() => Value = DefaultValue;
}

/// <summary>A setting that is on or off, with a sentence for each state.</summary>
public sealed class ToggleSetting : SettingItem
{
    private readonly string _whenOn;
    private readonly string _whenOff;
    private readonly Action<bool>? _changed;
    private bool _isOn;

    public ToggleSetting(
        string title,
        string explanation,
        string effect,
        bool defaultValue,
        string whenOn,
        string whenOff,
        Action<bool>? changed = null,
        bool advanced = false)
        : base(title, explanation, effect, advanced)
    {
        DefaultValue = defaultValue;
        _whenOn = whenOn ?? throw new ArgumentNullException(nameof(whenOn));
        _whenOff = whenOff ?? throw new ArgumentNullException(nameof(whenOff));
        _changed = changed;
        _isOn = defaultValue;
    }

    public bool DefaultValue { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
            {
                return;
            }

            _isOn = value;
            Raise();
            RaiseValueChanged();
            _changed?.Invoke(value);
        }
    }

    public override string Summary => _isOn ? _whenOn : _whenOff;

    public override string DefaultText => DefaultValue ? "on" : "off";

    public override bool IsDefault => _isOn == DefaultValue;

    public void Load(bool value)
    {
        _isOn = value;
        Raise(nameof(IsOn));
        RaiseValueChanged();
    }

    public override void Reset() => IsOn = DefaultValue;
}

/// <summary>
/// A named set of settings that belong together, with a line saying what the set is for.
/// </summary>
/// <remarks>
/// A group is either everyday or advanced, and so is each setting in it: an advanced group vanishes
/// whole when Advanced is off, and an everyday group can still hold one expert dial that appears
/// alongside the rest when it is on.
/// </remarks>
public sealed class SettingGroup : ViewModelBase
{
    private bool _isVisible = true;

    public SettingGroup(string title, string caption, bool advanced, params SettingItem[] items)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Caption = caption ?? throw new ArgumentNullException(nameof(caption));
        IsAdvanced = advanced;
        Items = new ObservableCollection<SettingItem>(items ?? []);
    }

    public string Title { get; }

    /// <summary>What this group of settings decides, so the heading is not the only clue.</summary>
    public string Caption { get; }

    public bool IsAdvanced { get; }

    public ObservableCollection<SettingItem> Items { get; }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    /// <summary>How many settings in this group are away from their default.</summary>
    public int ChangedCount => Items.Count(item => !item.IsDefault);

    public string ChangedText => ChangedCount switch
    {
        0 => "every setting here is at its default",
        1 => "1 setting changed from its default",
        var count => string.Create(CultureInfo.CurrentCulture, $"{count} settings changed from their defaults"),
    };

    /// <summary>
    /// Shows or hides this group and its settings for the state of the Advanced toggle. An
    /// advanced group with nothing visible in it is hidden rather than left as an empty heading.
    /// </summary>
    public void ApplyAdvanced(bool advanced)
    {
        foreach (var item in Items)
        {
            item.IsVisible = advanced || !item.IsAdvanced;
        }

        IsVisible = (advanced || !IsAdvanced) && Items.Any(item => item.IsVisible);
    }

    public void Refresh()
    {
        Raise(nameof(ChangedCount));
        Raise(nameof(ChangedText));
    }
}
