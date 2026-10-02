using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Luna.Models;

public abstract partial class SettingsItem : ObservableObject
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public partial class ToggleSetting : SettingsItem
{
    [ObservableProperty]
    private bool _value;
}

public partial class NumberSetting : SettingsItem
{
    private bool _isUpdating;

    [ObservableProperty]
    private string _displayText = "";

    public double DefaultValue { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; } = 1;
    public string Unit { get; set; } = string.Empty;

    public double NumericValue
    {
        get => double.TryParse(DisplayText, out var v) ? v : DefaultValue;
        set { if (!_isUpdating) DisplayText = Math.Clamp(value, Min, Max).ToString(); }
    }

    partial void OnDisplayTextChanged(string value)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        if (double.TryParse(value, out var num))
        {
            num = Math.Clamp(num, Min, Max);
            var corrected = num.ToString();
            if (corrected != value)
                DisplayText = corrected;
        }
        else
        {
            DisplayText = DefaultValue.ToString();
        }
        _isUpdating = false;
    }

    [RelayCommand]
    private void Increment()
    {
        NumericValue = Math.Min(NumericValue + Step, Max);
    }

    [RelayCommand]
    private void Decrement()
    {
        NumericValue = Math.Max(NumericValue - Step, Min);
    }

    [RelayCommand]
    private void Reset()
    {
        NumericValue = DefaultValue;
    }
}

public class SelectOption : ObservableObject
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public partial class SelectSetting : SettingsItem
{
    [ObservableProperty]
    private SelectOption? _selectedOption;

    [ObservableProperty]
    private bool _isOpen;

    public List<SelectOption> Options { get; set; } = [];
    public string DefaultValue { get; set; } = string.Empty;

    public string SelectedLabel => SelectedOption?.Label ?? "";

    partial void OnSelectedOptionChanged(SelectOption? value)
    {
        OnPropertyChanged(nameof(SelectedLabel));
        IsOpen = false;
    }

    [RelayCommand]
    private void SelectOption(SelectOption? option)
    {
        if (option != null) SelectedOption = option;
    }

    [RelayCommand]
    private void Reset()
    {
        SelectedOption = Options.FirstOrDefault(o => o.Value == DefaultValue) ?? Options.FirstOrDefault();
    }

    [RelayCommand]
    private void ToggleOpen()
    {
        IsOpen = !IsOpen;
    }
}

public partial class StringSetting : SettingsItem
{
    public const string MaskText = "········";

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string _displayText = string.Empty;

    public bool IsEncrypted { get; set; }
    public string DefaultValue { get; set; } = string.Empty;

    [RelayCommand]
    private void Reset()
    {
        Value = string.Empty;
        DisplayText = string.Empty;
    }
}