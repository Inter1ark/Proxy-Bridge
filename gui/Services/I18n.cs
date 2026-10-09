using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Avalonia.Data;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// App localization (Russian and English only). Strings live in the embedded
/// Assets/i18n/ru.json and Assets/i18n/en.json files as flat "key": "text" pairs.
///
/// XAML: <c>{i:Tr dash.connect}</c> (short form) or
/// <c>{Binding [dash.connect], Source={x:Static i:I18n.Instance}}</c>.
/// Both update live when <see cref="SetLanguage"/> is called.
/// Code: <c>I18n.T("key", args)</c>. A missing key falls back to English, then to the key itself.
/// </summary>
public sealed class I18n : INotifyPropertyChanged
{
    public const string Russian = "ru";
    public const string English = "en";

    public static I18n Instance { get; } = new();

    private readonly Dictionary<string, string> _en;
    private readonly Dictionary<string, string> _ru;
    private Dictionary<string, string> _current;
    private string _language = English;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the calling thread after the language changed (view models re-raise their localized properties).</summary>
    public event Action? LanguageChanged;

    private I18n()
    {
        _en = Load(English);
        _ru = Load(Russian);
        _current = _en;
    }

    /// <summary>"ru" or "en".</summary>
    public string Language => _language;

    public bool IsRussian => _language == Russian;

    /// <summary>Incremented on every language change; <see cref="TrExtension"/> bindings listen to it.</summary>
    public int Revision { get; private set; }

    /// <summary>Culture used for number and date formatting in the current language.</summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_current.TryGetValue(key, out var s)) return s;
        if (_en.TryGetValue(key, out s)) return s;
        return key;
    }

    public bool Has(string key) => _current.ContainsKey(key) || _en.ContainsKey(key);

    public static string T(string key, params object[] args)
    {
        var text = Instance.Get(key);
        if (args == null || args.Length == 0) return text;
        try
        {
            return string.Format(Instance.Culture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>Russian for ru/uk/be/kk system UI cultures, English otherwise.</summary>
    public static string DetectSystemLanguage()
    {
        var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return code is "ru" or "uk" or "be" or "kk" ? Russian : English;
    }

    /// <summary>Applies the language stored in config.json, or the system language when none was chosen.</summary>
    public void InitializeFromConfig()
    {
        try
        {
            var config = ConfigManager.LoadConfig();
            Initialize(config.LanguageChosen ? config.Language : "");
        }
        catch
        {
            Initialize("");
        }
    }

    /// <summary>Applies the configured language ("" or unknown means auto-detect).</summary>
    public void Initialize(string? configured)
    {
        var lang = Normalize(configured);
        SetLanguage(lang ?? DetectSystemLanguage());
    }

    public void SetLanguage(string language)
    {
        var lang = Normalize(language) ?? English;
        var changed = lang != _language || _current != (lang == Russian ? _ru : _en);
        _language = lang;
        _current = lang == Russian ? _ru : _en;
        Culture = CultureInfo.GetCultureInfo(lang == Russian ? "ru-RU" : "en-US");
        if (!changed) return;

        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRussian)));
        LanguageChanged?.Invoke();
    }

    /// <summary>Switches the language and stores it in config.json.</summary>
    public void SetLanguageAndSave(string language)
    {
        SetLanguage(language);
        try
        {
            var config = ConfigManager.LoadConfig();
            config.Language = _language;
            config.LanguageChosen = true;
            ConfigManager.SaveConfig(config);
        }
        catch
        {
            // the choice still applies for this session
        }
    }

    private static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var l = language.Trim().ToLowerInvariant();
        if (l.StartsWith(Russian, StringComparison.Ordinal)) return Russian;
        if (l.StartsWith(English, StringComparison.Ordinal)) return English;
        return null;
    }

    private static Dictionary<string, string> Load(string language)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"i18n.{language}.json");
            if (stream == null) return map;
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[I18n] failed to load {language}: {ex.Message}");
        }
        return map;
    }
}

/// <summary>
/// XAML shorthand: <c>Text="{i:Tr settings.language}"</c> is a live one-way binding that returns
/// <c>I18n.Instance[settings.language]</c> and re-evaluates whenever the language changes
/// (it listens to <see cref="I18n.Revision"/>, a plain property, so no indexer notification is needed).
/// </summary>
public sealed class TrExtension
{
    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; }

    public IBinding ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding(nameof(I18n.Revision))
        {
            Source = I18n.Instance,
            Mode = BindingMode.OneWay,
            Converter = new KeyConverter(Key)
        };
    }

    private sealed class KeyConverter : Avalonia.Data.Converters.IValueConverter
    {
        private readonly string _key;
        public KeyConverter(string key) => _key = key;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => I18n.Instance.Get(_key);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => BindingOperations.DoNothing;
    }
}
