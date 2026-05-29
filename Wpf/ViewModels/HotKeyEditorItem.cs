using RaycastPM.Models;

namespace RaycastPM.ViewModels;

public sealed class HotKeyEditorItem : ObservableObject
{
    private readonly HotKeyGesture _defaultGesture;
    private readonly Action<HotKeyEditorItem> _onChanged;
    private ModifierKeys _modifiers;
    private string _key;
    private bool _isLoading;

    public HotKeyEditorItem(
        string label,
        AppSection section,
        HotKeyGesture gesture,
        HotKeyGesture defaultGesture,
        IReadOnlyList<string> keyOptions,
        RelayCommand openCommand,
        Action<HotKeyEditorItem> onChanged)
    {
        Label = label;
        Section = section;
        _defaultGesture = defaultGesture;
        _modifiers = gesture.Modifiers == ModifierKeys.None ? defaultGesture.Modifiers : gesture.Modifiers;
        _key = string.IsNullOrWhiteSpace(gesture.Key) ? defaultGesture.Key : gesture.Key;
        KeyOptions = keyOptions;
        OpenCommand = openCommand;
        _onChanged = onChanged;
        ResetCommand = new RelayCommand(Reset);
    }

    public string Label { get; }
    public AppSection Section { get; }
    public IReadOnlyList<string> KeyOptions { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand ResetCommand { get; }

    public HotKeyGesture Gesture => new(_modifiers, _key);
    public string DisplayText => Gesture.ToString();

    public bool UseControl
    {
        get => _modifiers.HasFlag(ModifierKeys.Control);
        set => SetModifier(ModifierKeys.Control, value);
    }

    public bool UseAlt
    {
        get => _modifiers.HasFlag(ModifierKeys.Alt);
        set => SetModifier(ModifierKeys.Alt, value);
    }

    public bool UseShift
    {
        get => _modifiers.HasFlag(ModifierKeys.Shift);
        set => SetModifier(ModifierKeys.Shift, value);
    }

    public bool UseWin
    {
        get => _modifiers.HasFlag(ModifierKeys.Win);
        set => SetModifier(ModifierKeys.Win, value);
    }

    public string Key
    {
        get => _key;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? _defaultGesture.Key : value;
            if (SetProperty(ref _key, next))
            {
                OnPropertyChanged(nameof(DisplayText));
                NotifyChanged();
            }
        }
    }

    private void SetModifier(ModifierKeys modifier, bool enabled)
    {
        var next = enabled ? _modifiers | modifier : _modifiers & ~modifier;
        if (next == ModifierKeys.None || next == _modifiers)
        {
            OnPropertyChanged(nameof(UseControl));
            OnPropertyChanged(nameof(UseAlt));
            OnPropertyChanged(nameof(UseShift));
            OnPropertyChanged(nameof(UseWin));
            return;
        }

        _modifiers = next;
        OnPropertyChanged(nameof(UseControl));
        OnPropertyChanged(nameof(UseAlt));
        OnPropertyChanged(nameof(UseShift));
        OnPropertyChanged(nameof(UseWin));
        OnPropertyChanged(nameof(DisplayText));
        NotifyChanged();
    }

    private void Reset()
    {
        _isLoading = true;
        _modifiers = _defaultGesture.Modifiers;
        _key = _defaultGesture.Key;
        OnPropertyChanged(nameof(UseControl));
        OnPropertyChanged(nameof(UseAlt));
        OnPropertyChanged(nameof(UseShift));
        OnPropertyChanged(nameof(UseWin));
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(DisplayText));
        _isLoading = false;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (!_isLoading)
        {
            _onChanged(this);
        }
    }
}
