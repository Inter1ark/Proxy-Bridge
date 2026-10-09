using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

/// <summary>Region or city choice; Id 0 means "any".</summary>
public sealed class StoreGeoChoice
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public bool IsAny => Id == 0;
    public string Display => IsAny ? I18n.T(Name) : Name;
}

/// <summary>A purchased proxy in the "My purchases" list.</summary>
public sealed class StoreProxyRow : ViewModelBase
{
    private bool _isInList;
    private bool _busy;

    public StoreProxyRow(StoreProxyInfo info, bool isInList)
    {
        Info = info;
        _isInList = isInList;
    }

    public StoreProxyInfo Info { get; }
    public string CountryCode => Info.Country.Code;

    public string Title
    {
        get
        {
            var parts = new List<string> { Info.Country.Name };
            if (!string.IsNullOrEmpty(Info.City)) parts.Add(Info.City!);
            else if (!string.IsNullOrEmpty(Info.State)) parts.Add(Info.State!);
            return string.Join(", ", parts);
        }
    }

    public string TypeText => Info.Kind == "dc" ? I18n.T("store.kind.dc") : I18n.T("store.type." + Info.Type);

    public string RotationText => Info.Kind != "traffic" ? "" : Info.Rotation switch
    {
        "interval" => I18n.T("store.rot.interval_n", Info.Ttl ?? 0),
        "request" => I18n.T("store.rot.request"),
        _ => I18n.T("store.rot.static")
    };

    public bool IsActive => Info.Status == "active";
    public bool IsProblem => Info.Status is "exhausted" or "expired";
    public bool IsProvisioning => Info.Status == "provisioning";

    public string StatusText => Info.Status switch
    {
        "active" => I18n.T("store.status.active"),
        "exhausted" => I18n.T("store.status.exhausted"),
        "expired" => I18n.T("store.status.expired"),
        _ => I18n.T("store.status.provisioning")
    };

    public string DetailText
    {
        get
        {
            if (Info.Kind == "traffic")
            {
                var used = (Info.GbUsed ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
                return I18n.T("store.detail.traffic", used, Info.GbTotal ?? 0);
            }
            if (Info.ExpiresAt is DateTime e)
            {
                var date = e.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                return Info.RenewPending ? I18n.T("store.detail.until_renewed", date) : I18n.T("store.detail.until", date);
            }
            return "";
        }
    }

    public double UsedPercent => Info.Kind == "traffic" && (Info.GbTotal ?? 0) > 0
        ? Math.Min(100, (Info.GbUsed ?? 0) * 100.0 / Info.GbTotal!.Value)
        : 0;

    public bool ShowUsage => Info.Kind == "traffic";
    public string HostPort => Info.IsReady ? $"{Info.Host}:{Info.Port}" : "";
    public bool IsReady => Info.IsReady;
    public bool CanRenew => Info.CanRenew;
    public bool CanTopup => Info.CanTopup;
    public bool CanRefreshIp => Info.CanRefreshIp;

    public bool IsInList { get => _isInList; set { if (SetProperty(ref _isInList, value)) OnPropertyChanged(nameof(CanAddToList)); } }
    public bool CanAddToList => Info.IsReady && !_isInList && Info.Status == "active";
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public void RefreshTexts()
    {
        foreach (var n in new[] { nameof(Title), nameof(TypeText), nameof(RotationText), nameof(StatusText), nameof(DetailText) })
            OnPropertyChanged(n);
    }
}

/// <summary>
/// "Get proxy" page: buy a dedicated proxy for a fixed term or a pay per GB proxy, pay in the
/// browser, wait for the server to create it, then add it to the user's proxy list.
/// </summary>
public sealed class StoreViewModel : ViewModelBase
{
    private readonly StoreService _api = StoreService.Instance;
    private readonly Func<string, bool> _addToList;
    private readonly Func<string, bool> _isInList;
    private readonly Func<Window?> _window;

    private StoreCatalog? _catalog;
    private bool _isLoading;
    private bool _loaded;
    private string _mode = "dc";             // dc | traffic | renew | topup
    private string _returnMode = "dc";
    private StoreProxyRow? _target;

    private StoreCountry? _dcCountry;
    private StoreCountry? _country;
    private StoreGeoChoice? _state;
    private StoreGeoChoice? _city;
    private string _type = "residential";
    private string _rotation = "static";
    private string _ttlText = "10";
    private string _gbText = "1";
    private string _method = "yookassa";
    private string _availability = "";      // "" | checking | yes | no
    private int? _price;
    private bool _isPaying;
    private bool _isWaiting;
    private string _payUrl = "";
    private CancellationTokenSource? _waitCts;
    private CancellationTokenSource? _quoteCts;
    private int _geoVersion;
    private bool _geoLoading;

    public StoreViewModel(Func<string, bool> addToList, Func<string, bool> isInList, Func<Window?> window)
    {
        _addToList = addToList;
        _isInList = isInList;
        _window = window;

        ShowDcCommand = new RelayCommand(() => Mode = "dc");
        ShowTrafficCommand = new RelayCommand(() => Mode = "traffic");
        SetTypeCommand = new RelayCommand<string>(t => { if (t != null) Type = t; });
        SetRotationCommand = new RelayCommand<string>(r => { if (r != null) Rotation = r; });
        SetMethodCommand = new RelayCommand<string>(m => { if (m != null) Method = m; });
        GbMinusCommand = new RelayCommand(() => Gb = Math.Max(MinGb, Gb - 1));
        GbPlusCommand = new RelayCommand(() => Gb = Math.Min(MaxGb, Gb + 1));
        PayCommand = new RelayCommand(async () => await PayAsync());
        CancelWaitCommand = new RelayCommand(CancelWait);
        OpenPayPageCommand = new RelayCommand(() => OpenUrl(_payUrl));
        BackCommand = new RelayCommand(() => { _target = null; Mode = _returnMode; });
        ReloadCommand = new RelayCommand(async () => await LoadAsync(force: true));
        RefreshListCommand = new RelayCommand(async () => await RefreshPurchasesAsync());
        CopyCommand = new RelayCommand<StoreProxyRow>(async r => await CopyAsync(r));
        AddToListCommand = new RelayCommand<StoreProxyRow>(AddRowToList);
        RenewCommand = new RelayCommand<StoreProxyRow>(r => StartTargeted(r, "renew"));
        TopupCommand = new RelayCommand<StoreProxyRow>(r => StartTargeted(r, "topup"));
        RefreshIpCommand = new RelayCommand<StoreProxyRow>(async r => await RefreshIpAsync(r));

        I18n.Instance.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------
    public ICommand ShowDcCommand { get; }
    public ICommand ShowTrafficCommand { get; }
    public ICommand SetTypeCommand { get; }
    public ICommand SetRotationCommand { get; }
    public ICommand SetMethodCommand { get; }
    public ICommand GbMinusCommand { get; }
    public ICommand GbPlusCommand { get; }
    public ICommand PayCommand { get; }
    public ICommand CancelWaitCommand { get; }
    public ICommand OpenPayPageCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RefreshListCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand AddToListCommand { get; }
    public ICommand RenewCommand { get; }
    public ICommand TopupCommand { get; }
    public ICommand RefreshIpCommand { get; }

    /// <summary>Status line of the purchase panel.</summary>
    public LocStatus Status { get; } = new();
    /// <summary>Status line of the purchases list.</summary>
    public LocStatus ListStatus { get; } = new();

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) RaiseLoad(); } }
    public bool IsLoaded { get => _loaded; private set { if (SetProperty(ref _loaded, value)) RaiseLoad(); } }
    public bool ShowForm => _loaded && !_isLoading;
    public bool ShowLoadError => !_loaded && !_isLoading && Status.HasText;

    private void RaiseLoad()
    {
        OnPropertyChanged(nameof(ShowForm));
        OnPropertyChanged(nameof(ShowLoadError));
        OnPropertyChanged(nameof(CanPay));
    }
    public bool HasDc => _catalog?.HasDc == true;
    public bool HasTraffic => _catalog?.HasTraffic == true;

    /// <summary>Called when the page is shown.</summary>
    public async Task OnShownAsync()
    {
        await LoadAsync(force: false);
        await RefreshPurchasesAsync();
    }

    private async Task LoadAsync(bool force)
    {
        if (IsLoading || (_loaded && !force)) return;
        IsLoading = true;
        Status.Clear();
        try
        {
            _catalog = await _api.GetCatalogAsync();
            FillCountries();
            IsLoaded = true;
            if (!HasDc && HasTraffic) Mode = "traffic";
            if (!HasDc && !HasTraffic) Status.Set("store.err.empty", StatusKind.Warning);
        }
        catch (StoreException ex)
        {
            Status.Set(ErrorKey(ex.Code), StatusKind.Error);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasDc));
            OnPropertyChanged(nameof(HasTraffic));
            RaisePrice();
        }
    }

    public ObservableCollection<StoreCountry> DcCountries { get; } = new();
    public ObservableCollection<StoreCountry> TrafficCountries { get; } = new();
    public ObservableCollection<StoreGeoChoice> States { get; } = new();
    public ObservableCollection<StoreGeoChoice> Cities { get; } = new();

    private void FillCountries()
    {
        if (_catalog == null) return;
        var dcCode = _dcCountry?.Code;
        var trCode = _country?.Code;
        Reset(DcCountries, _catalog.DcCountries.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
        Reset(TrafficCountries, _catalog.TrafficCountries.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
        _dcCountry = DcCountries.FirstOrDefault(c => c.Code == (dcCode ?? "DE")) ?? DcCountries.FirstOrDefault();
        _country = TrafficCountries.FirstOrDefault(c => c.Code == (trCode ?? "DE")) ?? TrafficCountries.FirstOrDefault();
        OnPropertyChanged(nameof(DcCountry));
        OnPropertyChanged(nameof(Country));
        if (trCode == null && _country != null) _ = LoadStatesAsync();
        OnPropertyChanged(nameof(MinGb));
        OnPropertyChanged(nameof(MaxGb));
        RaiseTypePrices();
    }

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void OnLanguageChanged()
    {
        Status.Refresh();
        ListStatus.Refresh();
        foreach (var r in Purchases) r.RefreshTexts();
        // country names are language dependent: rebuild the lists, keeping the selection
        FillCountries();
        var st = _state?.Id ?? 0;
        var ct = _city?.Id ?? 0;
        RebuildGeo(States, States.Where(s => !s.IsAny).ToList(), "store.any_region");
        RebuildGeo(Cities, Cities.Where(s => !s.IsAny).ToList(), "store.any_city");
        _state = States.FirstOrDefault(s => s.Id == st) ?? States.FirstOrDefault();
        _city = Cities.FirstOrDefault(c => c.Id == ct) ?? Cities.FirstOrDefault();
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(City));
        RaiseTypePrices();
        RaisePrice();
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(TargetTitle));
    }

    // ------------------------------------------------------------------
    // Mode
    // ------------------------------------------------------------------
    public string Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            if (value is "dc" or "traffic") _returnMode = value;
            OnPropertyChanged(nameof(IsDcMode));
            OnPropertyChanged(nameof(IsTrafficMode));
            OnPropertyChanged(nameof(IsRenewMode));
            OnPropertyChanged(nameof(IsTopupMode));
            OnPropertyChanged(nameof(IsTargetMode));
            OnPropertyChanged(nameof(IsPickMode));
            OnPropertyChanged(nameof(ShowGb));
            OnPropertyChanged(nameof(TargetTitle));
            Status.Clear();
            RaisePrice();
            if (value == "traffic" && _availability == "") _ = CheckAvailabilityAsync();
        }
    }

    public bool IsDcMode => _mode == "dc";
    public bool IsTrafficMode => _mode == "traffic";
    public bool IsRenewMode => _mode == "renew";
    public bool IsTopupMode => _mode == "topup";
    public bool IsTargetMode => _mode is "renew" or "topup";
    public bool IsPickMode => _mode is "dc" or "traffic";
    public bool ShowGb => _mode is "traffic" or "topup";

    public string TargetTitle => _target == null ? "" : $"{_target.Title}  ·  {_target.TypeText}";

    private void StartTargeted(StoreProxyRow? row, string mode)
    {
        if (row == null || IsWaiting) return;
        _target = row;
        if (mode == "topup") Gb = Math.Max(MinGb, 1);
        Mode = mode;
        OnPropertyChanged(nameof(TargetTitle));
    }

    // ------------------------------------------------------------------
    // Dedicated
    // ------------------------------------------------------------------
    public StoreCountry? DcCountry
    {
        get => _dcCountry;
        set { if (SetProperty(ref _dcCountry, value)) RaisePrice(); }
    }

    public string DcPriceText => _catalog == null ? "" : I18n.T("store.dc.price", _catalog.DcPriceRub, _catalog.DcDays);
    public int DcDays => _catalog?.DcDays ?? 30;

    // ------------------------------------------------------------------
    // Traffic: geo
    // ------------------------------------------------------------------
    public StoreCountry? Country
    {
        get => _country;
        set
        {
            if (!SetProperty(ref _country, value)) return;
            _ = LoadStatesAsync();
            _ = CheckAvailabilityAsync();
        }
    }

    public StoreGeoChoice? State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            _ = LoadCitiesAsync();
            RaisePrice();
        }
    }

    public StoreGeoChoice? City
    {
        get => _city;
        set { if (SetProperty(ref _city, value)) RaisePrice(); }
    }

    public bool GeoLoading { get => _geoLoading; private set => SetProperty(ref _geoLoading, value); }
    public bool HasCities => Cities.Count > 1;

    private static void RebuildGeo(ObservableCollection<StoreGeoChoice> target, List<StoreGeoChoice> items, string anyKey)
    {
        target.Clear();
        target.Add(new StoreGeoChoice { Id = 0, Name = anyKey });
        foreach (var i in items.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)) target.Add(i);
    }

    private async Task LoadStatesAsync()
    {
        var version = ++_geoVersion;
        RebuildGeo(States, new(), "store.any_region");
        RebuildGeo(Cities, new(), "store.any_city");
        _state = States[0];
        _city = Cities[0];
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(City));
        OnPropertyChanged(nameof(HasCities));
        if (_country == null || _country.Id == 0) return;
        GeoLoading = true;
        try
        {
            var list = await _api.GetStatesAsync(_country.Id);
            if (version != _geoVersion) return;
            RebuildGeo(States, list.Select(s => new StoreGeoChoice { Id = s.Id, Name = s.Name }).ToList(), "store.any_region");
            _state = States[0];
            OnPropertyChanged(nameof(State));
        }
        catch (StoreException)
        {
            // regions are optional: the country alone still works
        }
        finally
        {
            if (version == _geoVersion) GeoLoading = false;
        }
    }

    private async Task LoadCitiesAsync()
    {
        var version = ++_geoVersion;
        RebuildGeo(Cities, new(), "store.any_city");
        _city = Cities[0];
        OnPropertyChanged(nameof(City));
        OnPropertyChanged(nameof(HasCities));
        if (_country == null || _state == null || _state.IsAny) return;
        GeoLoading = true;
        try
        {
            var list = await _api.GetCitiesAsync(_country.Id, _state.Id);
            if (version != _geoVersion) return;
            RebuildGeo(Cities, list.Select(s => new StoreGeoChoice { Id = s.Id, Name = s.Name }).ToList(), "store.any_city");
            _city = Cities[0];
            OnPropertyChanged(nameof(City));
            OnPropertyChanged(nameof(HasCities));
        }
        catch (StoreException)
        {
        }
        finally
        {
            if (version == _geoVersion) GeoLoading = false;
        }
    }

    // ------------------------------------------------------------------
    // Traffic: type, rotation, amount
    // ------------------------------------------------------------------
    public string Type
    {
        get => _type;
        set
        {
            if (!SetProperty(ref _type, value)) return;
            OnPropertyChanged(nameof(IsMobile));
            OnPropertyChanged(nameof(IsResidential));
            OnPropertyChanged(nameof(IsDatacenter));
            RaisePrice();
            _ = CheckAvailabilityAsync();
        }
    }

    public bool IsMobile => _type == "mobile";
    public bool IsResidential => _type == "residential";
    public bool IsDatacenter => _type == "datacenter";

    public string MobilePrice => GbPriceText("mobile");
    public string ResidentialPrice => GbPriceText("residential");
    public string DatacenterPrice => GbPriceText("datacenter");

    private string GbPriceText(string type) =>
        _catalog != null && _catalog.GbPriceRub.TryGetValue(type, out var p)
            ? I18n.T("store.per_gb", Math.Ceiling(p).ToString("0", CultureInfo.InvariantCulture))
            : "";

    private void RaiseTypePrices()
    {
        OnPropertyChanged(nameof(MobilePrice));
        OnPropertyChanged(nameof(ResidentialPrice));
        OnPropertyChanged(nameof(DatacenterPrice));
        OnPropertyChanged(nameof(DcPriceText));
        OnPropertyChanged(nameof(DcDays));
    }

    public string Rotation
    {
        get => _rotation;
        set
        {
            if (!SetProperty(ref _rotation, value)) return;
            OnPropertyChanged(nameof(IsStatic));
            OnPropertyChanged(nameof(IsInterval));
            OnPropertyChanged(nameof(IsPerRequest));
            RaisePrice();
        }
    }

    public bool IsStatic => _rotation == "static";
    public bool IsInterval => _rotation == "interval";
    public bool IsPerRequest => _rotation == "request";

    public string TtlText
    {
        get => _ttlText;
        set { if (SetProperty(ref _ttlText, value)) RaisePrice(); }
    }

    private int? Ttl => int.TryParse(_ttlText, out var t) && t >= 1 && t <= (_catalog?.TtlMax ?? 1440) ? t : null;

    public int MinGb => _catalog?.MinGb ?? 1;
    public int MaxGb => _catalog?.MaxGb ?? 100;

    public string GbText
    {
        get => _gbText;
        set { if (SetProperty(ref _gbText, value)) RaisePrice(); }
    }

    private int Gb
    {
        get => int.TryParse(_gbText, out var g) ? g : 0;
        set => GbText = value.ToString(CultureInfo.InvariantCulture);
    }

    private bool GbValid => Gb >= MinGb && Gb <= MaxGb;

    // ------------------------------------------------------------------
    // Availability (does the vendor have this type in the country right now)
    // ------------------------------------------------------------------
    public string AvailabilityText => _availability switch
    {
        "checking" => I18n.T("store.avail.checking"),
        "no" => I18n.T("store.avail.no"),
        _ => ""
    };

    public bool IsUnavailable => _availability == "no";

    private async Task CheckAvailabilityAsync()
    {
        if (_country == null || _catalog?.HasTraffic != true) return;
        var code = _country.Code;
        var type = _type;
        SetAvailability("checking");
        try
        {
            var ok = await _api.IsAvailableAsync(code, type);
            if (_country?.Code == code && _type == type) SetAvailability(ok ? "yes" : "no");
        }
        catch (StoreException)
        {
            if (_country?.Code == code && _type == type) SetAvailability("yes");
        }
    }

    private void SetAvailability(string value)
    {
        _availability = value;
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(CanPay));
    }

    // ------------------------------------------------------------------
    // Payment method and price
    // ------------------------------------------------------------------
    public string Method
    {
        get => _method;
        set
        {
            if (!SetProperty(ref _method, value)) return;
            OnPropertyChanged(nameof(IsCard));
            OnPropertyChanged(nameof(IsCrypto));
        }
    }

    public bool IsCard => _method == "yookassa";
    public bool IsCrypto => _method == "cryptobot";

    public string PriceText => _price is int p ? p.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + " ₽" : "--";
    public string PayButtonText => _price is int p
        ? I18n.T("store.pay_n", p.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")))
        : I18n.T("store.pay");

    public string PriceNote => _mode switch
    {
        "dc" or "renew" => I18n.T("store.note.dc", DcDays),
        _ when GbValid => I18n.T("store.note.gb", Gb),
        _ => I18n.T("store.err.bad_gb_range", MinGb, MaxGb)
    };

    public bool CanPay => ShowForm && !_isPaying && !_isWaiting && _price.HasValue && Validate() == null
                          && !(IsTrafficMode && IsUnavailable);

    /// <summary>Local estimate right away, then the exact server price.</summary>
    private void RaisePrice()
    {
        _price = Estimate();
        NotifyPrice();
        _ = QuoteAsync();
    }

    private void NotifyPrice()
    {
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(PayButtonText));
        OnPropertyChanged(nameof(PriceNote));
        OnPropertyChanged(nameof(CanPay));
    }

    private int? Estimate()
    {
        if (_catalog == null || Validate() != null) return null;
        switch (_mode)
        {
            case "dc":
            case "renew":
                return _catalog.DcPriceRub;
            case "traffic":
                return _catalog.GbPriceRub.TryGetValue(_type, out var p) ? (int)Math.Ceiling(p * Gb - 1e-9) : null;
            case "topup":
                var t = _target?.Info.Type ?? "";
                return _catalog.GbPriceRub.TryGetValue(t, out var q) ? (int)Math.Ceiling(q * Gb - 1e-9) : null;
        }
        return null;
    }

    private async Task QuoteAsync()
    {
        _quoteCts?.Cancel();
        if (_catalog == null || Validate() != null) return;
        var cts = _quoteCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            var (product, parameters) = BuildOrder();
            var amount = await _api.QuoteAsync(product, parameters, cts.Token);
            if (cts.IsCancellationRequested) return;
            _price = amount;
            NotifyPrice();
        }
        catch (OperationCanceledException)
        {
        }
        catch (StoreException ex) when (ex.Code == "unavailable")
        {
            SetAvailability("no");
        }
        catch (StoreException)
        {
            // keep the local estimate; the order call reports real problems
        }
    }

    /// <summary>Returns an i18n error key, or null when the form is complete.</summary>
    private string? Validate()
    {
        switch (_mode)
        {
            case "dc":
                return _dcCountry == null ? "store.err.pick_country" : null;
            case "renew":
                return _target == null ? "store.err.pick_country" : null;
            case "traffic":
                if (_country == null) return "store.err.pick_country";
                if (!GbValid) return "store.err.bad_gb";
                if (_rotation == "interval" && Ttl == null) return "store.err.bad_ttl";
                return null;
            case "topup":
                return _target == null ? "store.err.pick_country" : !GbValid ? "store.err.bad_gb" : null;
        }
        return null;
    }

    private (string product, JsonObject parameters) BuildOrder()
    {
        switch (_mode)
        {
            case "dc":
                return ("dc", new JsonObject { ["country"] = _dcCountry?.Code });
            case "renew":
                return ("dc_renew", new JsonObject { ["proxy_id"] = _target?.Info.Id });
            case "topup":
                return ("topup", new JsonObject { ["proxy_id"] = _target?.Info.Id, ["gb"] = Gb });
            default:
                var p = new JsonObject
                {
                    ["country"] = _country?.Code,
                    ["type"] = _type,
                    ["rotation"] = _rotation,
                    ["gb"] = Gb
                };
                if (_rotation == "interval") p["ttl"] = Ttl;
                if (_state is { IsAny: false }) p["state_id"] = _state.Id;
                if (_state is { IsAny: false } && _city is { IsAny: false }) p["city_id"] = _city.Id;
                return ("traffic", p);
        }
    }

    // ------------------------------------------------------------------
    // Pay and wait
    // ------------------------------------------------------------------
    public bool IsPaying { get => _isPaying; private set { if (SetProperty(ref _isPaying, value)) OnPropertyChanged(nameof(CanPay)); } }

    public bool IsWaiting
    {
        get => _isWaiting;
        private set
        {
            if (!SetProperty(ref _isWaiting, value)) return;
            OnPropertyChanged(nameof(CanPay));
            OnPropertyChanged(nameof(IsNotWaiting));
        }
    }

    public bool IsNotWaiting => !_isWaiting;

    private async Task PayAsync()
    {
        var error = Validate();
        if (error != null)
        {
            Status.Set(error, StatusKind.Warning, MinGb, MaxGb);
            return;
        }
        IsPaying = true;
        Status.Set("store.creating", StatusKind.Neutral);
        try
        {
            var (product, parameters) = BuildOrder();
            var (token, url, amount) = await _api.CreateOrderAsync(product, parameters, _method);
            _price = amount;
            NotifyPrice();
            _payUrl = url;
            OpenUrl(url);
            _ = WaitAsync(token);
        }
        catch (StoreException ex)
        {
            Status.Set(ErrorKey(ex.Code), StatusKind.Error);
        }
        finally
        {
            IsPaying = false;
        }
    }

    private async Task WaitAsync(string token)
    {
        _waitCts?.Cancel();
        var cts = _waitCts = new CancellationTokenSource();
        IsWaiting = true;
        Status.Set("store.wait.payment", StatusKind.Neutral);
        var started = DateTime.UtcNow;
        var failures = 0;
        try
        {
            while (!cts.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(40))
            {
                await Task.Delay(3000, cts.Token);
                StoreOrderStatus st;
                try
                {
                    st = await _api.GetOrderAsync(token, cts.Token);
                    failures = 0;
                }
                catch (StoreException)
                {
                    if (++failures >= 20) { Status.Set("store.err.network", StatusKind.Error); return; }
                    continue;
                }
                switch (st.State)
                {
                    case "pending":
                        Status.Set("store.wait.payment", StatusKind.Neutral);
                        break;
                    case "processing":
                        Status.Set("store.wait.creating", StatusKind.Neutral);
                        break;
                    case "done":
                        OnOrderDone(st);
                        return;
                    case "failed":
                        Status.Set(st.Refunded ? "store.failed.refunded" : "store.failed.support", StatusKind.Error);
                        await RefreshPurchasesAsync();
                        return;
                    case "canceled":
                        Status.Set("store.canceled", StatusKind.Warning);
                        return;
                }
            }
            if (!cts.IsCancellationRequested) Status.Set("store.wait.timeout", StatusKind.Warning);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_waitCts == cts) IsWaiting = false;
        }
    }

    private void OnOrderDone(StoreOrderStatus st)
    {
        var added = false;
        if (st.Proxy is { IsReady: true } p && p.Status == "active")
            added = _addToList(p.ProxyUrl);
        Status.Set(_mode switch
        {
            "renew" => "store.done.renew",
            "topup" => "store.done.topup",
            _ => added ? "store.done.added" : "store.done"
        }, StatusKind.Success);
        if (IsTargetMode)
        {
            _target = null;
            _mode = _returnMode;
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(IsDcMode));
            OnPropertyChanged(nameof(IsTrafficMode));
            OnPropertyChanged(nameof(IsRenewMode));
            OnPropertyChanged(nameof(IsTopupMode));
            OnPropertyChanged(nameof(IsTargetMode));
            OnPropertyChanged(nameof(IsPickMode));
            OnPropertyChanged(nameof(ShowGb));
            RaisePrice();
        }
        _ = RefreshPurchasesAsync();
    }

    private void CancelWait()
    {
        _waitCts?.Cancel();
        IsWaiting = false;
        Status.Set("store.wait.stopped", StatusKind.Neutral);
        _ = RefreshPurchasesAsync();
    }

    // ------------------------------------------------------------------
    // Purchases
    // ------------------------------------------------------------------
    public ObservableCollection<StoreProxyRow> Purchases { get; } = new();
    public bool HasPurchases => Purchases.Count > 0;
    public bool HasNoPurchases => Purchases.Count == 0;
    private bool _listLoading;
    public bool ListLoading { get => _listLoading; private set => SetProperty(ref _listLoading, value); }

    public async Task RefreshPurchasesAsync()
    {
        if (ListLoading) return;
        ListLoading = true;
        try
        {
            var list = await _api.GetProxiesAsync();
            Purchases.Clear();
            foreach (var p in list)
                Purchases.Add(new StoreProxyRow(p, p.IsReady && _isInList(p.ProxyUrl)));
            ListStatus.Clear();
        }
        catch (StoreException ex)
        {
            ListStatus.Set(ErrorKey(ex.Code), StatusKind.Error);
        }
        finally
        {
            ListLoading = false;
            OnPropertyChanged(nameof(HasPurchases));
            OnPropertyChanged(nameof(HasNoPurchases));
        }
    }

    /// <summary>The user's proxy list changed (a proxy was deleted or added by hand).</summary>
    public void SyncInList()
    {
        foreach (var r in Purchases)
            r.IsInList = r.Info.IsReady && _isInList(r.Info.ProxyUrl);
    }

    private void AddRowToList(StoreProxyRow? row)
    {
        if (row == null || !row.Info.IsReady) return;
        if (_addToList(row.Info.ProxyUrl)) ListStatus.Set("store.list.added", StatusKind.Success);
        row.IsInList = true;
    }

    private async Task CopyAsync(StoreProxyRow? row)
    {
        if (row == null || !row.Info.IsReady) return;
        try
        {
            var clipboard = _window()?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(row.Info.ProxyUrl);
            ListStatus.Set("store.list.copied", StatusKind.Success);
        }
        catch (Exception ex)
        {
            AppLog.Warn("store copy failed: " + ex.GetType().Name);
        }
    }

    private async Task RefreshIpAsync(StoreProxyRow? row)
    {
        if (row == null || row.Busy) return;
        row.Busy = true;
        try
        {
            await _api.RefreshIpAsync(row.Info.Id);
            ListStatus.Set("store.list.ip_changed", StatusKind.Success);
        }
        catch (StoreException ex)
        {
            ListStatus.Set(ErrorKey(ex.Code), StatusKind.Error);
        }
        finally
        {
            row.Busy = false;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private static string ErrorKey(string code) => code switch
    {
        "network" => "store.err.network",
        "out_of_stock" => "store.err.out_of_stock",
        "unavailable" => "store.err.unavailable",
        "renew_pending" => "store.err.renew_pending",
        "cannot_renew" => "store.err.cannot_renew",
        "cannot_topup" => "store.err.cannot_topup",
        "too_often" => "store.err.too_often",
        "not_activated" or "not_found" or "revoked" or "expired" => "store.err.license",
        "provider_error" => "store.err.payment",
        "bad_gb" => "store.err.bad_gb",
        "bad_ttl" => "store.err.bad_ttl",
        "store_disabled" => "store.err.disabled",
        _ => "store.err.server"
    };

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to open payment page", ex);
        }
    }
}
