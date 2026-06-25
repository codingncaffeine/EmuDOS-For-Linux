using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EmuDOS.Core.Downloads;
using EmuDOS.Core.Model;
using EmuDOS.Services;
using EmuDOS.ViewModels;

namespace EmuDOS.Views;

/// <summary>
/// Tabbed preferences. "Game Options" edits one game's curated DOSBox settings (a per-game override);
/// the rest hold art accounts, media folders, hotkeys, on-demand downloads, backups, cloud sync, and
/// About/updates. Ported to Avalonia: WPF dialogs → StorageProvider/ConfirmDialog, PasswordBox →
/// PasswordChar TextBox, the CRT-shader download is a placeholder until the shader phase lands.
/// </summary>
public partial class PreferencesWindow : Window
{
    private static readonly IBrush Pending = new SolidColorBrush(Color.FromRgb(0xA8, 0x9A, 0x86));
    private static readonly IBrush Success = new SolidColorBrush(Color.FromRgb(0x9F, 0xE0, 0xA0));
    private static readonly IBrush Failure = new SolidColorBrush(Color.FromRgb(0xE0, 0x85, 0x85));

    private static readonly int[] MemoryPresets = [4, 8, 16, 32, 64];

    private readonly AppServices _services;
    private readonly GameTile? _game;
    private GameProfile? _profile;

    public PreferencesWindow(AppServices services, GameTile? game = null)
    {
        InitializeComponent();
        _services = services;
        _game = game;

        SsUser.Text = services.Settings.ScreenScraperUser;
        SsPass.Text = services.Settings.ScreenScraperPassword;
        SgdbKey.Text = services.Settings.SteamGridDbKey;
        Use3DBox.IsChecked = services.Settings.Use3DBoxes;

        ScreenshotFolderBox.Text = string.IsNullOrWhiteSpace(services.Settings.ScreenshotFolder)
            ? services.Paths.ScreenshotsDir : services.Settings.ScreenshotFolder;
        VideoFolderBox.Text = string.IsNullOrWhiteSpace(services.Settings.VideoFolder)
            ? services.Paths.VideosDir : services.Settings.VideoFolder;
        ScreenshotSizeBox.SelectedIndex = services.Settings.ScreenshotOriginalSize ? 0 : 1;
        VideoQualityBox.SelectedIndex = services.Settings.VideoQuality switch { "Low" => 0, "High" => 2, _ => 1 };

        HotkeyScreenshot.Text = Display(services.Settings.ScreenshotKey, "F12");
        HotkeyRecord.Text = Display(services.Settings.RecordKey, "F9");
        HotkeyMouseLock.Text = Display(services.Settings.MouseLockKey, "Middle Mouse");
        HotkeyMenu.Text = Display(services.Settings.MenuKey, "F10");
        HotkeySaveState.Text = Display(services.Settings.SaveStateKey, "F5");
        HotkeyLoadState.Text = Display(services.Settings.LoadStateKey, "F8");
        HotkeyCheat.Text = Display(services.Settings.CheatKey, "F11");
        HotkeyFastForward.Text = Display(services.Settings.FastForwardKey, "F6");
        HotkeySlowMotion.Text = Display(services.Settings.SlowMotionKey, "F7");
        HotkeyRewind.Text = Display(services.Settings.RewindKey, "F4");
        HotkeyPause.Text = Display(services.Settings.PauseKey, "Pause");
        HotkeyShaderCycle.Text = Display(services.Settings.ShaderCycleKey, "F3");
        HotkeyFps.Text = Display(services.Settings.FpsOverlayKey, "F1");

        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        CheckUpdatesBox.IsChecked = services.Settings.CheckForUpdates;
        Hardware3dfxBox.IsChecked = services.Settings.Hardware3dfx;
        _ = RefreshLatestVersionAsync();

        UpdateCloudUi();

        var downloadRows = AssetManifest.All
            .Select(a => new DownloadRow(a, _services.Downloads.IsInstalled(a)))
            .ToList();
        // CRT shaders: the libretro slang preset pack. The librashader GL engine is bundled with
        // EmuDOS (next to the binary), so this only downloads the presets; F3 in-game cycles them.
        var paths = _services.Paths;
        downloadRows.Add(new DownloadRow(
            "CRT shaders",
            "The libretro slang shader collection (CRT, scanlines, monitors). Press F3 in a game to cycle CRT presets. The shader engine is bundled — no extra install needed.",
            installed: Effects.Librashader.ShaderDownloader.IsInstalled(paths.SlangShaderRoot, paths.LibrashaderDllPath),
            customDownload: report => Effects.Librashader.ShaderDownloader.DownloadAsync(paths.SlangShaderRoot, paths.LibrashaderDllPath, report)));
        DownloadList.ItemsSource = downloadRows;

        bool hasRoms = _services.SystemFiles.HasMt32;
        Set(Mt32RomStatus,
            hasRoms ? "✓ MT-32 ROMs detected" : "✗ Not found — drag the ROMs in to enable MT-32 audio",
            hasRoms ? Success : Pending);

        if (game is null)
        {
            GameOptionsTab.IsVisible = false;
            Tabs.SelectedIndex = 1; // Snaps
        }
        else
        {
            _profile = _services.Store.ReadProfile(game.Game.GameboxPath);
            PopulateGameOptions();
            Tabs.SelectedItem = GameOptionsTab;
        }
    }

    // ── Game Options ────────────────────────────────────────────────────────────
    private void PopulateGameOptions()
    {
        if (_profile is null)
            return;

        GameTitle.Text = _profile.Title;

        CpuCyclesMode.ItemsSource = Enum.GetValues<CyclesMode>();
        CpuCyclesMode.SelectedItem = _profile.Cpu.CyclesMode;
        FixedCycles.Text = _profile.Cpu.FixedCycles > 0 ? _profile.Cpu.FixedCycles.ToString() : "60000";
        UpdateCyclesEnabled();

        MachineTypeBox.ItemsSource = Enum.GetValues<MachineType>();
        MachineTypeBox.SelectedItem = _profile.Machine.Machine;

        MemoryBox.ItemsSource = MemoryPresets.Contains(_profile.Memory.SizeMb)
            ? MemoryPresets
            : MemoryPresets.Append(_profile.Memory.SizeMb).OrderBy(x => x).ToArray();
        MemoryBox.SelectedItem = _profile.Memory.SizeMb;

        SoundCardBox.ItemsSource = Enum.GetValues<SoundBlasterType>();
        SoundCardBox.SelectedItem = _profile.Sound.SoundBlaster;

        MidiBox.ItemsSource = Enum.GetValues<MidiDevice>();
        MidiBox.SelectedItem = _profile.Sound.Midi;

        AspectBox.IsChecked = _profile.Machine.AspectCorrection;
        BrightnessSlider.Value = _profile.Display.Brightness;
        GammaSlider.Value = _profile.Display.Gamma;

        GameOptionsStatus.Text = string.Empty;
    }

    private void OnCyclesModeChanged(object? sender, SelectionChangedEventArgs e) => UpdateCyclesEnabled();

    private void UpdateCyclesEnabled()
    {
        bool fixedMode = CpuCyclesMode.SelectedItem is CyclesMode.Fixed;
        if (FixedCycles is not null)
            FixedCycles.IsEnabled = fixedMode;
        if (CyclesHint is not null)
            CyclesHint.IsVisible = fixedMode;
    }

    private void OnSaveGameOptions(object? sender, RoutedEventArgs e)
    {
        if (_game is null || _profile is null)
            return;

        int cycles = int.TryParse(FixedCycles.Text, out var c) && c > 0 ? c : _profile.Cpu.FixedCycles;
        var updated = _profile with
        {
            Cpu = _profile.Cpu with { CyclesMode = (CyclesMode)CpuCyclesMode.SelectedItem!, FixedCycles = cycles },
            Machine = _profile.Machine with
            {
                Machine = (MachineType)MachineTypeBox.SelectedItem!,
                AspectCorrection = AspectBox.IsChecked == true,
            },
            Memory = _profile.Memory with { SizeMb = (int)MemoryBox.SelectedItem! },
            Sound = _profile.Sound with
            {
                SoundBlaster = (SoundBlasterType)SoundCardBox.SelectedItem!,
                Midi = (MidiDevice)MidiBox.SelectedItem!,
            },
            Display = _profile.Display with { Brightness = BrightnessSlider.Value, Gamma = GammaSlider.Value },
            Origin = ProfileOrigin.UserOverride,
        };

        _services.Store.WriteProfile(_game.Game.GameboxPath, updated);
        _profile = updated;
        Set(GameOptionsStatus, "Saved — applies next launch.", Success);
    }

    private void OnResetGameOptions(object? sender, RoutedEventArgs e)
    {
        if (_game is null || _profile is null)
            return;

        var contentDir = _services.Store.Resolve(_game.Game.GameboxPath).ContentPath;
        var names = Directory.Exists(contentDir)
            ? Directory.EnumerateFiles(contentDir).Select(Path.GetFileName).OfType<string>()
            : Enumerable.Empty<string>();

        var baseline = _profile with { Origin = ProfileOrigin.Default };
        var resolved = _services.Resolver.Resolve(baseline, names);
        _services.Store.WriteProfile(_game.Game.GameboxPath, resolved);
        _profile = resolved;

        PopulateGameOptions();
        Set(GameOptionsStatus, "Reset to catalog default.", Success);
    }

    private static string Display(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    // ── Hotkeys ─────────────────────────────────────────────────────────────────
    private void OnHotkeyCapture(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            box.Text = box == HotkeyMouseLock ? "Middle Mouse"
                : box == HotkeyRecord ? "F9"
                : box == HotkeyMenu ? "F10"
                : box == HotkeySaveState ? "F5"
                : box == HotkeyLoadState ? "F8"
                : "F12";
            return;
        }
        box.Text = e.Key.ToString();
    }

    private void OnSaveHotkeys(object? sender, RoutedEventArgs e)
    {
        var s = _services.Settings;
        s.ScreenshotKey = HotkeyScreenshot.Text!.Trim();
        s.RecordKey = HotkeyRecord.Text!.Trim();
        s.MouseLockKey = HotkeyMouseLock.Text == "Middle Mouse" ? string.Empty : HotkeyMouseLock.Text!.Trim();
        s.MenuKey = HotkeyMenu.Text!.Trim();
        s.SaveStateKey = HotkeySaveState.Text!.Trim();
        s.LoadStateKey = HotkeyLoadState.Text!.Trim();
        s.CheatKey = HotkeyCheat.Text!.Trim();
        s.FastForwardKey = HotkeyFastForward.Text!.Trim();
        s.SlowMotionKey = HotkeySlowMotion.Text!.Trim();
        s.RewindKey = HotkeyRewind.Text!.Trim();
        s.PauseKey = HotkeyPause.Text!.Trim();
        s.ShaderCycleKey = HotkeyShaderCycle.Text!.Trim();
        s.FpsOverlayKey = HotkeyFps.Text!.Trim();
        _services.SettingsStore.Save(s);
        Set(HotkeysStatus, "Saved — applies next launch.", Success);
    }

    // ── Media (folders / sizes) ───────────────────────────────────────────────────
    private async void OnBrowseScreenshotFolder(object? sender, RoutedEventArgs e) => await BrowseInto(ScreenshotFolderBox);
    private async void OnBrowseVideoFolder(object? sender, RoutedEventArgs e) => await BrowseInto(VideoFolderBox);

    private async Task BrowseInto(TextBox target)
    {
        var picked = await PickFolderAsync("Choose a folder", target.Text);
        if (picked is not null)
            target.Text = picked;
    }

    private async Task<string?> PickFolderAsync(string title, string? start)
    {
        var opts = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var folders = await StorageProvider.OpenFolderPickerAsync(opts);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnSaveMedia(object? sender, RoutedEventArgs e)
    {
        _services.Settings.ScreenshotFolder = ScreenshotFolderBox.Text!.Trim();
        _services.Settings.VideoFolder = VideoFolderBox.Text!.Trim();
        _services.Settings.ScreenshotOriginalSize = ScreenshotSizeBox.SelectedIndex == 0;
        _services.Settings.VideoQuality = VideoQualityBox.SelectedIndex switch { 0 => "Low", 2 => "High", _ => "Medium" };
        _services.SettingsStore.Save(_services.Settings);
        Set(MediaStatus, "Saved.", Success);
    }

    private void OnToggle3DBoxes(object? sender, RoutedEventArgs e)
    {
        _services.Settings.Use3DBoxes = Use3DBox.IsChecked == true;
        _services.SettingsStore.Save(_services.Settings);
    }

    // ── Snaps (art accounts) ──────────────────────────────────────────────────────
    private async void OnLoginScreenScraper(object? sender, RoutedEventArgs e)
    {
        SsLogin.IsEnabled = false;
        Set(SsStatus, "Testing…", Pending);

        _services.Settings.ScreenScraperUser = SsUser.Text!.Trim();
        _services.Settings.ScreenScraperPassword = SsPass.Text ?? string.Empty;
        _services.SettingsStore.Save(_services.Settings);

        var (ok, maxThreads) = await _services.ValidateScreenScraperAsync(
            _services.Settings.ScreenScraperUser, _services.Settings.ScreenScraperPassword);
        if (ok)
        {
            _services.Settings.ScreenScraperMaxThreads = maxThreads;
            _services.SettingsStore.Save(_services.Settings);
            _services.ReloadArtService();
            TriggerArtRefetch();
        }

        Set(SsStatus,
            ok ? $"✓ Logged in as {_services.Settings.ScreenScraperUser} ({maxThreads} thread{(maxThreads == 1 ? "" : "s")})"
               : "✗ Login failed",
            ok ? Success : Failure);
        SsLogin.IsEnabled = true;
    }

    private async void OnLoginSteamGridDb(object? sender, RoutedEventArgs e)
    {
        SgdbLogin.IsEnabled = false;
        Set(SgdbStatus, "Testing…", Pending);

        _services.Settings.SteamGridDbKey = SgdbKey.Text!.Trim();
        _services.SettingsStore.Save(_services.Settings);

        bool ok = await _services.ValidateSteamGridDbAsync(_services.Settings.SteamGridDbKey);
        if (ok)
        {
            _services.ReloadArtService();
            TriggerArtRefetch();
        }

        Set(SgdbStatus, ok ? "✓ Key valid — fetching missing covers…" : "✗ Invalid key", ok ? Success : Failure);
        SgdbLogin.IsEnabled = true;
    }

    private void TriggerArtRefetch()
    {
        if (Owner is MainWindow main)
            _ = main.RefetchMissingArtAsync();
    }

    // ── Downloads ─────────────────────────────────────────────────────────────────
    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DownloadRow row)
            return;

        row.IsBusy = true;
        if (row.CustomDownload is { } custom)
        {
            try { await custom(msg => row.SetProgress(msg)); row.SetResult(true, null); }
            catch (Exception ex) { row.SetResult(false, ex.Message); }
            row.IsBusy = false;
            return;
        }

        var progress = new Progress<DownloadProgress>(p =>
            row.SetProgress(p.Fraction is double f ? $"Downloading… {f:P0}" : "Downloading…"));
        var result = await _services.Downloads.DownloadAsync(row.Asset!, progress);
        row.SetResult(result.Success, result.Error);
        row.IsBusy = false;
    }

    // ── Backups ─────────────────────────────────────────────────────────────────
    private async void OnBackupDatabase(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Choose a folder for the database backup", null);
        if (folder is null)
            return;
        try
        {
            var src = Path.Combine(_services.Paths.DataRoot, "library.db");
            var dest = Path.Combine(folder, $"library-{DateTime.Now:yyyy-MM-dd-HHmm}.db");
            File.Copy(src, dest, overwrite: false);
            Set(BackupStatus, $"Database backed up to {dest}", Success);
        }
        catch (Exception ex) { Set(BackupStatus, $"Backup failed: {ex.Message}", Failure); }
    }

    private async void OnRestoreDatabase(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a database backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Database") { Patterns = ["*.db"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } file)
            return;
        if (!await ConfirmDialog.ShowAsync(this, "Restore database",
                "Restore this database on the next launch? It replaces your current favorites, play counts and history, and EmuDOS will need to restart.",
                "Restore"))
            return;
        try
        {
            File.Copy(file, Core.Library.LibraryDatabase.PendingRestorePath(_services.Paths), overwrite: true);
            Set(BackupStatus, "Restore staged — restart EmuDOS to apply it.", Success);
        }
        catch (Exception ex) { Set(BackupStatus, $"Restore failed: {ex.Message}", Failure); }
    }

    private async void OnBackupAllSaves(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Choose where to save the backup archive", null);
        if (folder is null)
            return;
        try
        {
            var dest = Path.Combine(folder, $"emudos-saves-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");
            int games = Core.Library.SaveBackup.CreateAllSavesArchive(_services.Paths.GameboxesDir, dest);
            Set(BackupStatus, $"Backed up saves for {games} game(s) to {dest}", Success);
        }
        catch (Exception ex) { Set(BackupStatus, $"Backup failed: {ex.Message}", Failure); }
    }

    // ── Cloud sync ─────────────────────────────────────────────────────────────────
    private EmuDOS.Metadata.GitHubSyncService? _gh;
    private EmuDOS.Metadata.GitHubSyncService Gh => _gh ??= new EmuDOS.Metadata.GitHubSyncService(_services.CloudLog.Info);

    private void UpdateCloudUi()
    {
        var connected = !string.IsNullOrEmpty(_services.Settings.GitHubToken);
        CloudStatus.Text = connected ? $"Connected as {_services.Settings.GitHubLogin}." : "Not connected.";
        ConnectButton.IsVisible = !connected;
        SyncNowButton.IsEnabled = connected;
        DisconnectButton.IsVisible = connected;
        CloudPassphrase.Text = _services.Settings.CloudEncryptionPassphrase;
    }

    private byte[]? CloudKey()
    {
        var pass = CloudPassphrase.Text ?? string.Empty;
        if (_services.Settings.CloudEncryptionPassphrase != pass)
        {
            _services.Settings.CloudEncryptionPassphrase = pass;
            _services.SettingsStore.Save(_services.Settings);
        }
        return string.IsNullOrEmpty(pass) ? null : EmuDOS.Metadata.CloudCrypto.DeriveKey(pass);
    }

    private async void OnConnectGitHub(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            var code = await Gh.RequestDeviceCodeAsync();
            if (code is null)
            {
                CloudStatus.Text = "Couldn't start GitHub login. Check your connection.";
                return;
            }
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
                try
                {
                    var transfer = new DataTransfer();
                    transfer.Add(DataTransferItem.CreateText(code.UserCode));
                    await cb.SetDataAsync(transfer);
                }
                catch { /* clipboard may be busy */ }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(code.VerificationUri) { UseShellExecute = true }); }
            catch { /* user can open it manually */ }
            CloudStatus.Text = $"Enter code  {code.UserCode}  at {code.VerificationUri} (copied to clipboard). Waiting for authorization…";

            var token = await Gh.PollAccessTokenAsync(code);
            if (token is null)
            {
                CloudStatus.Text = "Login timed out or was denied. Try again.";
                return;
            }
            _services.Settings.GitHubToken = token;
            _services.Settings.GitHubLogin = await Gh.GetLoginAsync(token) ?? "";
            _services.SettingsStore.Save(_services.Settings);
            _services.CloudLog.Info($"Connected as {_services.Settings.GitHubLogin}.");
            UpdateCloudUi();
        }
        catch (Exception ex) { CloudStatus.Text = $"Connect failed: {ex.Message}"; }
        finally { ConnectButton.IsEnabled = true; }
    }

    private async void OnSyncNow(object? sender, RoutedEventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        CloudStatus.Text = "Syncing…";
        var progress = new Progress<string>(s => CloudStatus.Text = s);
        try
        {
            var key = CloudKey();
            var s = _services.Settings;
            var result = await Gh.SyncAsync(s.GitHubToken, s.GitHubLogin, s.GitHubRepo,
                _services.Paths.GameboxesDir, Path.Combine(_services.Paths.DataRoot, "library.db"), progress, encKey: key);
            CloudStatus.Text = result.Ok
                ? $"Synced — {result.Uploaded} uploaded, {result.Downloaded} downloaded."
                : $"Sync failed: {result.Error}";
        }
        catch (Exception ex) { CloudStatus.Text = $"Sync failed: {ex.Message}"; }
        finally { SyncNowButton.IsEnabled = true; }
    }

    private void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        _services.CloudLog.Info("Disconnected.");
        _services.Settings.GitHubToken = string.Empty;
        _services.Settings.GitHubLogin = string.Empty;
        _services.SettingsStore.Save(_services.Settings);
        UpdateCloudUi();
        CloudStatus.Text = "Disconnected.";
    }

    private static void Set(TextBlock target, string text, IBrush brush)
    {
        target.Text = text;
        target.Foreground = brush;
    }

    // ── About / updates ───────────────────────────────────────────────────────────
    private AppUpdate? _latestRelease;

    private async Task RefreshLatestVersionAsync()
    {
        var release = await UpdateService.LatestReleaseAsync();
        _latestRelease = release;
        if (release is null)
        {
            LatestVersionText.Text = "Latest on GitHub: couldn't check (offline?).";
            UpdateNowButton.IsVisible = false;
            return;
        }
        var tag = release.Tag.TrimStart('v', 'V');
        LatestVersionText.Text = release.IsNewer
            ? $"Latest on GitHub: {tag} — update available."
            : $"Latest on GitHub: {tag} — you're up to date.";
        UpdateNowButton.IsVisible = release.IsNewer;
    }

    private async void OnUpdateNow(object? sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { IsNewer: true } update)
            return;
        if (!await ConfirmDialog.ShowAsync(this, "Update EmuDOS",
                $"Download and install EmuDOS {update.Tag.TrimStart('v', 'V')} now?\nEmuDOS will restart to finish.", "Update"))
            return;

        UpdateNowButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        var progress = new Progress<string>(s => LatestVersionText.Text = s);
        try { await UpdateService.ApplyAsync(update, progress); }
        catch (Exception ex)
        {
            LatestVersionText.Text = $"Update failed: {ex.Message}";
            UpdateNowButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        LatestVersionText.Text = "Checking…";
        await RefreshLatestVersionAsync();
        CheckUpdatesButton.IsEnabled = true;
    }

    private void OnOpenReleases(object? sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateService.ReleasesUrl) { UseShellExecute = true }); }
        catch { /* user can open it manually */ }
    }

    private void OnToggleCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        _services.Settings.CheckForUpdates = CheckUpdatesBox.IsChecked == true;
        _services.SettingsStore.Save(_services.Settings);
    }

    private void OnToggleHardware3dfx(object? sender, RoutedEventArgs e)
    {
        _services.Settings.Hardware3dfx = Hardware3dfxBox.IsChecked == true;
        _services.SettingsStore.Save(_services.Settings);
    }
}
