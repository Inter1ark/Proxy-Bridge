using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

/// <summary>
/// UI-side adapter for the update window: wraps <see cref="UpdateViewModel.Shared"/> (state and data only)
/// and turns it into localized texts and visibility flags. Commands are bound straight to <see cref="U"/>.
/// </summary>
public sealed class UpdatePresenter : ViewModelBase, IDisposable
{
    public UpdatePresenter(UpdateViewModel model)
    {
        U = model;
        U.PropertyChanged += OnModelChanged;
        I18n.Instance.LanguageChanged += RaiseAll;
    }

    public UpdateViewModel U { get; }

    public void Dispose()
    {
        U.PropertyChanged -= OnModelChanged;
        I18n.Instance.LanguageChanged -= RaiseAll;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

    private void RaiseAll() => OnPropertyChanged(string.Empty);

    // ---- state flags ----
    // Idle is shown as "checking": the window is only opened right before or after a check starts.
    public bool IsChecking => U.State is UpdateState.Checking or UpdateState.Idle;
    public bool IsUpToDate => U.State == UpdateState.UpToDate;
    public bool IsAvailable => U.State == UpdateState.Available;
    public bool IsDownloading => U.State == UpdateState.Downloading;
    public bool IsVerifying => U.State == UpdateState.Verifying;
    public bool IsInstalling => U.State == UpdateState.Installing;
    public bool IsError => U.State == UpdateState.Error;
    public bool IsProgressState => IsDownloading || IsVerifying || IsInstalling;

    public bool ShowLater => !U.IsMandatory;
    public bool ShowSkip => !U.IsMandatory;
    public bool IsMandatory => U.IsMandatory;
    public bool CanRetryInstall => IsError && U.CanInstall;
    public bool CanRetryCheck => IsError && !U.CanInstall;

    // ---- texts ----
    private string CurrentVersionValue => string.IsNullOrWhiteSpace(U.CurrentVersion) ? LicenseService.AppVersion : U.CurrentVersion;

    public string AvailableTitle => I18n.T("upd.available_title", U.NewVersion);

    public string VersionLine => string.IsNullOrWhiteSpace(U.NewVersion)
        ? I18n.T("upd.current_version", CurrentVersionValue)
        : I18n.T("upd.version_line", CurrentVersionValue, U.NewVersion);

    public string UpToDateText => I18n.T("upd.uptodate_text", CurrentVersionValue);

    public string PublishedText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(U.PublishedAt)) return "";
            return DateTime.TryParse(U.PublishedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? I18n.T("upd.published", d.ToLocalTime().ToString("dd.MM.yyyy"))
                : "";
        }
    }

    public bool HasPublished => PublishedText.Length > 0;

    /// <summary>Release notes in the current language (falls back to the other one), one item per line.</summary>
    public IReadOnlyList<string> NotesLines
    {
        get
        {
            var text = I18n.Instance.IsRussian
                ? (string.IsNullOrWhiteSpace(U.NotesRu) ? U.NotesEn : U.NotesRu)
                : (string.IsNullOrWhiteSpace(U.NotesEn) ? U.NotesRu : U.NotesEn);
            return (text ?? "")
                .Replace("\r", "")
                .Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', '•').Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
    }

    public bool HasNotes => NotesLines.Count > 0;

    public string ProgressText
    {
        get
        {
            if (IsVerifying) return I18n.T("upd.verifying");
            if (U.BytesTotal <= 0) return I18n.T("upd.downloading");
            return I18n.T("upd.progress_mb", ToMb(U.BytesReceived), ToMb(U.BytesTotal));
        }
    }

    public string ProgressTitle => I18n.T(IsVerifying ? "upd.verifying_title" : IsInstalling ? "upd.installing_title" : "upd.downloading_title");

    public double ProgressValue => IsVerifying || IsInstalling ? 100 : Math.Clamp(U.Progress, 0, 100);

    public bool IsProgressIndeterminate => IsDownloading && U.BytesTotal <= 0 && U.Progress <= 0;

    public string ErrorText => I18n.T(U.ErrorCode switch
    {
        "network" => "upd.err.network",
        "hash_mismatch" => "upd.err.hash_mismatch",
        "download_failed" => "upd.err.download_failed",
        "install_failed" => "upd.err.install_failed",
        "unsupported_platform" => "upd.err.unsupported_platform",
        "server" => "upd.err.server",
        _ => "upd.err.unknown"
    });

    private static double ToMb(long bytes) => Math.Round(bytes / (1024.0 * 1024.0), 1);
}
