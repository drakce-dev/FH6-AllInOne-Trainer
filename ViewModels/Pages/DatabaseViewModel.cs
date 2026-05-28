using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FH6Mod.Cheats.Season;
using FH6Mod.Cheats.Sql;
using FH6Mod.Services;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace FH6Mod.ViewModels.Pages;

public partial class DatabaseViewModel : PageViewModelBase
{
    private readonly CheatService _cheats;
    private readonly GameProcessService _game;
    private readonly LogService _log;

    public override string PageTitle => "Database";
    public override string PageSubtitle => "Direct SQL writes to the game's in-memory CDatabase.";
    public override MaterialIconKind PageIcon => MaterialIconKind.DatabaseEditOutline;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] private bool _isFreeCarsLockOn;
    [ObservableProperty] private bool _isAutoshowLockOn;
    [ObservableProperty] private bool _isInstallFlagsLockOn;

    [ObservableProperty] private bool _canToggle;

    [ObservableProperty] private string _currentSeasonText = "Unknown";
    [ObservableProperty] private bool _seasonAvailable;

    public DatabaseViewModel() : this(
        App.Services.GetRequiredService<CheatService>(),
        App.Services.GetRequiredService<GameProcessService>(),
        App.Services.GetRequiredService<LogService>()) { }
    public DatabaseViewModel(CheatService cheats, GameProcessService game, LogService log)
    {
        _cheats = cheats;
        _game = game;
        _log = log;
        _game.StatusChanged += OnGameStatusChanged;
        CanToggle = _game.IsAttached;
        IsFreeCarsLockOn      = _cheats.IsSqlLockActive(SqlFeature.FreeCarPrices);
        IsAutoshowLockOn      = _cheats.IsSqlLockActive(SqlFeature.AutoshowUnlock);
        IsInstallFlagsLockOn  = _cheats.IsSqlLockActive(SqlFeature.InstallFlags);
    }

    private void OnGameStatusChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CanToggle = _game.IsAttached;
            if (!CanToggle)
            {
                StatusMessage = "FH6 is not running — start the game first.";
                SeasonAvailable = false;
                CurrentSeasonText = "Unknown";
            }
            else
            {
                RefreshSeason();
            }
        });
    }

    private void Run(SqlFeature f, string label)
    {
        var ok = _cheats.RunSql(f);
        StatusIsError = !ok;
        StatusMessage = ok ? $"{label} applied. Effect persists until game restart." : _cheats.LastError;
        AutoClearStatus();
    }

    private void AutoClearStatus()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(5000);
            StatusMessage = null;
        });
    }

    [RelayCommand]
    private void UnlockEverything()
    {
        var errors = new System.Collections.Generic.List<string>();
        var labels = new System.Collections.Generic.List<string>();

        void TryRun(SqlFeature f, string label)
        {
            var ok = _cheats.RunSql(f);
            if (ok) labels.Add(label);
            else errors.Add(label);
        }

        TryRun(SqlFeature.FreeCarPrices, "Free Cars");
        TryRun(SqlFeature.InstallFlags, "Install Flags");
        TryRun(SqlFeature.AutoshowUnlock, "Autoshow Unlock");
        TryRun(SqlFeature.ClearNewTag, "Clear NEW Tags");
        TryRun(SqlFeature.AddAllCars, "Add All Cars");
        TryRun(SqlFeature.FreeUpgrades, "Free Upgrades");
        TryRun(SqlFeature.FreeWheels, "Free Wheels");
        TryRun(SqlFeature.UnlockUpgradePresets, "Upgrade Presets");
        TryRun(SqlFeature.FullAutoshow, "Full Autoshow");

        StatusIsError = errors.Count > 0;
        StatusMessage = errors.Count == 0
            ? $"Unlock Everything applied — {string.Join(", ", labels)}. All cars free, visible, in garage, free upgrades & wheels."
            : $"Partially applied. Failed: {string.Join(", ", errors)}";
        AutoClearStatus();
    }

    [RelayCommand] private void ClearNewTag()     => Run(SqlFeature.ClearNewTag,     "Clear NEW tags");
    [RelayCommand] private void FreeCarPrices()   => Run(SqlFeature.FreeCarPrices,   "Free car prices");
    [RelayCommand] private void InstallFlags()    => Run(SqlFeature.InstallFlags,    "Install flags");
    [RelayCommand] private void AddAllCars()      => Run(SqlFeature.AddAllCars,      "Add All Cars grant");
    [RelayCommand] private void AutoshowUnlock()  => Run(SqlFeature.AutoshowUnlock,  "Autoshow visibility");
    [RelayCommand] private void FreeUpgrades()    => Run(SqlFeature.FreeUpgrades,    "Free upgrades (47 tables)");
    [RelayCommand] private void FreeWheels()      => Run(SqlFeature.FreeWheels,      "Free wheels");
    [RelayCommand] private void UnlockPresets()   => Run(SqlFeature.UnlockUpgradePresets, "Upgrade presets");
    [RelayCommand] private void FullAutoshow()    => Run(SqlFeature.FullAutoshow,    "Full autoshow (CarBuckets)");
    [RelayCommand] private void DriftScore()      => Run(SqlFeature.DriftScoreScalar, "Drift Score 10x");
    [RelayCommand] private void MaxTraction()     => Run(SqlFeature.MaxTraction,     "Max Traction (grip hack)");
    [RelayCommand] private void TorqueScale()     => Run(SqlFeature.TorqueScale,     "Torque Scale 2x");
    [RelayCommand] private void DragScale()       => Run(SqlFeature.DragScale,       "Reduce Drag 0.5x");

    [RelayCommand]
    private void ToggleFreeCarsLock()
    {
        var target = !_cheats.IsSqlLockActive(SqlFeature.FreeCarPrices);
        var ok = _cheats.ToggleSqlLock(SqlFeature.FreeCarPrices, target, periodSec: 10);
        IsFreeCarsLockOn = _cheats.IsSqlLockActive(SqlFeature.FreeCarPrices);
        StatusIsError = !ok;
        StatusMessage = ok
            ? (target ? "Free Cars LOCK ON — prices stay at 0 (re-applied every 10s)." : "Free Cars LOCK OFF — restored from backup.")
            : _cheats.LastError;
        AutoClearStatus();
    }

    [RelayCommand]
    private void ToggleAutoshowLock()
    {
        var target = !_cheats.IsSqlLockActive(SqlFeature.AutoshowUnlock);
        var ok = _cheats.ToggleSqlLock(SqlFeature.AutoshowUnlock, target, periodSec: 10);
        IsAutoshowLockOn = _cheats.IsSqlLockActive(SqlFeature.AutoshowUnlock);
        StatusIsError = !ok;
        StatusMessage = ok
            ? (target ? "Autoshow LOCK ON — every car visible (re-applied every 10s)." : "Autoshow LOCK OFF — restored from backup.")
            : _cheats.LastError;
        AutoClearStatus();
    }

    [RelayCommand]
    private void ToggleInstallFlagsLock()
    {
        var target = !_cheats.IsSqlLockActive(SqlFeature.InstallFlags);
        var ok = _cheats.ToggleSqlLock(SqlFeature.InstallFlags, target, periodSec: 10);
        IsInstallFlagsLockOn = _cheats.IsSqlLockActive(SqlFeature.InstallFlags);
        StatusIsError = !ok;
        StatusMessage = ok
            ? (target ? "Install Flags LOCK ON — every car stays Installed/Purchased/Drivable (re-applied every 10s)." : "Install Flags LOCK OFF — restored from backup.")
            : _cheats.LastError;
        AutoClearStatus();
    }

    private void RefreshSeason()
    {
        var s = _cheats.GetCurrentSeason();
        if (s >= 0 && s <= 3)
        {
            CurrentSeasonText = SeasonChanger.SeasonName(s);
            SeasonAvailable = true;
        }
        else
        {
            CurrentSeasonText = "Not loaded";
            SeasonAvailable = false;
        }
    }

    private void ApplySeason(int season, string label)
    {
        var ok = _cheats.SetSeason(season, out var err);
        StatusIsError = !ok;
        StatusMessage = ok ? $"Season set to {label}. Fast-travel or load a race to refresh visuals." : err;
        if (ok) RefreshSeason();
        AutoClearStatus();
    }

    [RelayCommand] private void SetSpring() => ApplySeason(0, "Spring");
    [RelayCommand] private void SetSummer() => ApplySeason(1, "Summer");
    [RelayCommand] private void SetAutumn() => ApplySeason(2, "Autumn");
    [RelayCommand] private void SetWinter() => ApplySeason(3, "Winter");

    [RelayCommand]
    private void RefreshSeasonStatus()
    {
        if (!_game.IsAttached) { CurrentSeasonText = "Not loaded"; SeasonAvailable = false; return; }
        RefreshSeason();
    }
}
