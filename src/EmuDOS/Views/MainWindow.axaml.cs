using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using EmuDOS.Controls;
using EmuDOS.Core.Downloads;
using EmuDOS.Core.Engine.DosBoxPure;
using EmuDOS.Core.Library;
using EmuDOS.Core.Model;
using EmuDOS.Services;
using EmuDOS.ViewModels;

namespace EmuDOS.Views;

/// <summary>Interaction logic for MainWindow.axaml — the library shelf.</summary>
/// <remarks>
/// Phase 2 scope: the shelf renders, games import (drop), art downloads, and the per-game/library
/// context actions for art, box style, favorites, and deletion work. Launching a game and the
/// secondary windows (detail card, Manage, Preferences, manual, cheats) are routed to a status note
/// and wired in Phases 3–4.
/// </remarks>
public partial class MainWindow : Window
{
    private GameTile? _dragTile;
    private ShelfPanel? _dragPanel;
    private Point _grabOffset;
    private bool _didDrag;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private AppServices Services => ((App)Application.Current!).Services;

    // Actions that need a window/engine ported in a later phase show a clear status note for now.
    private void ComingSoon(string what) =>
        Vm?.Report($"{what} — lands in a later port phase.", busy: false);

    // ── Library-wide actions (right-click empty shelf) ───────────────────────────────────────
    private void OnPreferences(object? sender, RoutedEventArgs e) => ComingSoon("Preferences");

    private async void OnDownloadMissingArt(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.FetchMissingArtAsync();
    }

    private async void OnDownload3DArtAll(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) await Vm.FetchAll3DArtAsync();
    }

    private void OnCloseFilter(object? sender, RoutedEventArgs e) => Vm?.ToggleFilter();

    // ── Keyboard shortcuts ───────────────────────────────────────────────────────────────────
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
            return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.F2)
        {
            Vm.IsEditMode = !Vm.IsEditMode;
            e.Handled = true;
        }
        else if (e.Key == Key.S && ctrl)
        {
            SaveLayout();
            e.Handled = true;
        }
        else if (e.Key == Key.A && ctrl)
        {
            Vm.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F && ctrl)
        {
            Vm.ToggleFilter();
            if (Vm.IsFilterOpen)
                this.FindControl<TextBox>("FilterSearch")?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (Vm.IsFilterOpen)
                Vm.ToggleFilter(); // close + clear the filter
            else
                Vm.ClearSelection();
        }
    }

    // ── Box pointer interactions (select / open / edit-mode drag) ────────────────────────────
    private void OnBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm?.IsEditMode != true || sender is not Control fe || fe.DataContext is not GameTile tile)
            return;
        if (!e.GetCurrentPoint(fe).Properties.IsLeftButtonPressed)
            return;

        _dragPanel = fe.GetVisualAncestors().OfType<ShelfPanel>().FirstOrDefault();
        if (_dragPanel is null)
            return;

        _dragTile = tile;
        _grabOffset = e.GetPosition(fe);
        _didDrag = false;
        e.Pointer.Capture(fe);
        e.Handled = true;
    }

    private void OnBoxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTile is null || _dragPanel is null
            || !e.GetCurrentPoint(_dragPanel).Properties.IsLeftButtonPressed)
            return;

        var p = e.GetPosition(_dragPanel);
        _dragTile.ManualLeft = p.X - _grabOffset.X;
        _dragTile.ManualBottom = p.Y - _grabOffset.Y + _dragTile.BoxHeight;
        _didDrag = true;
        _dragPanel.InvalidateArrange();
    }

    private void OnBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);

        if (Vm?.IsEditMode == true && _dragTile is not null && _dragPanel is not null && _didDrag)
        {
            // Full freedom — no snapping on either axis (the calibration captures exact placement).
            _dragPanel.InvalidateArrange();
            _dragTile = null;
            _dragPanel = null;
            e.Handled = true;
            return;
        }

        _dragTile = null;
        _dragPanel = null;

        if (e.InitialPressMouseButton != MouseButton.Left)
            return; // right-click is handled by ContextRequested
        if (Vm?.IsEditMode == true || sender is not Control { DataContext: GameTile tile })
            return;

        // Ctrl+click toggles selection (for delete); a plain click opens the game card.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            tile.IsSelected = !tile.IsSelected;
            e.Handled = true;
            return;
        }

        Vm?.ClearSelection();
        OpenGameCard(tile);
    }

    // Hover video preview (the Trinitron monitor popup) needs the LibVLC SnapPlayer — Phase 4.
    private void OnBoxPointerEntered(object? sender, PointerEventArgs e) { }

    private void OnBoxPointerExited(object? sender, PointerEventArgs e) { }

    /// <summary>The per-game detail card lands with the secondary windows in a later phase.</summary>
    private void OpenGameCard(GameTile tile) => ComingSoon($"Detail card for “{tile.Title}”");

    // ── Per-game right-click menu ────────────────────────────────────────────────────────────
    private void OnBoxContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: GameTile tile } element)
            return;
        e.Handled = true; // suppress the library menu underneath

        var menu = new ContextMenu();

        menu.Items.Add(Item("▶  Play", () => _ = LaunchGameAsync(tile)));

        var favorite = Item(tile.IsFavorite ? "♥  Favorited" : "♡  Favorite", () =>
        {
            tile.IsFavorite = !tile.IsFavorite;
            Services.Library.SetFavorite(tile.Id, tile.IsFavorite);
        });
        menu.Items.Add(favorite);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("⚙  Preferences", () => ComingSoon("Game preferences")));
        menu.Items.Add(Item("🛠  Manage…", () => ComingSoon("Manage window")));
        menu.Items.Add(Item("📂  Open game folder", () =>
        {
            try { Process.Start(new ProcessStartInfo(tile.Game.GameboxPath) { UseShellExecute = true }); }
            catch { /* folder may have been removed */ }
        }));
        menu.Items.Add(Item("🖥  Open in DOS", () => _ = LaunchGameAsync(tile, bootToDos: true)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("🖼  Download box art", () => _ = Vm?.DownloadArtAsync(tile)));
        menu.Items.Add(Item("🧊  Download 3D box art", () => _ = Vm?.Download3DArtAsync(tile)));
        menu.Items.Add(Item("📁  Set box art from file…", () => _ = SetCustomArtAsync(tile)));

        var boxStyle = new MenuItem { Header = "🎴  Box style" };
        foreach (var (label, style) in new[]
                 {
                     ("Default (follow global)", BoxStyle.Default),
                     ("2D box", BoxStyle.TwoD),
                     ("3D box", BoxStyle.ThreeD),
                 })
        {
            var captured = style;
            boxStyle.Items.Add(new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = tile.StyleOverride == style,
                Command = new RelayAction(() => Vm?.SetGameBoxStyle(tile, captured)),
            });
        }
        menu.Items.Add(boxStyle);
        menu.Items.Add(Item("✏  Rename from ScreenScraper…", () => ComingSoon("ScreenScraper rename dialog")));
        menu.Items.Add(Item("📖  Read manual", () => ComingSoon("Manual viewer")));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("🗑  Delete", () => Vm?.DeleteGames(new[] { tile })));

        menu.Open(element);
    }

    private static MenuItem Item(string header, Action onClick) =>
        new() { Header = header, Command = new RelayAction(onClick) };

    private async Task SetCustomArtAsync(GameTile tile)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose box art for {tile.Title}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] },
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path)
            return;
        try { Vm?.SetBoxArt(tile, File.ReadAllBytes(path)); }
        catch (Exception ex) { Vm?.Report($"Couldn't set box art: {ex.Message}", busy: false); }
    }

    private void DeleteSelected()
    {
        var selected = Vm?.Games.Where(g => g.IsSelected).ToList() ?? [];
        if (selected.Count == 0)
            return;
        // A modal confirm dialog lands with the dialogs in Phase 4; box art is preserved on delete,
        // so re-importing restores covers without a re-download.
        Vm!.DeleteGames(selected);
    }

    // ── Drag-and-drop import (files onto the window) ─────────────────────────────────────────
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null || e.DataTransfer?.Contains(DataFormat.File) != true)
            return;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
            return;
        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToArray();
        if (paths.Length > 0)
            await Vm.HandleDropAsync(paths);
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ── Edit-mode layout calibration (dev) ───────────────────────────────────────────────────
    private sealed record LayoutEntry(string Title, double Left, double Bottom);

    private void SaveLayout()
    {
        if (Vm is null)
            return;
        var placed = Vm.Games
            .Where(t => t.IsManuallyPlaced)
            .Select(t => new LayoutEntry(t.Title, Math.Round(t.ManualLeft!.Value, 1), Math.Round(t.ManualBottom!.Value, 1)))
            .OrderBy(o => o.Bottom).ThenBy(o => o.Left)
            .ToList();
        var path = Path.Combine(Services.Paths.DataRoot, "layout.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(placed, new JsonSerializerOptions { WriteIndented = true }));
            Vm.Report($"Saved {placed.Count} box positions to layout.json.", busy: false);
        }
        catch (Exception ex) { Vm.Report($"Couldn't save layout: {ex.Message}", busy: false); }
    }

    /// <summary>Re-attempt covers for games still missing one (after an art login/key change).</summary>
    public Task RefetchMissingArtAsync() => Vm?.FetchMissingArtAsync() ?? Task.CompletedTask;

    /// <summary>Launch the first game on the shelf — used by the EMUDOS_AUTOPLAY smoke hook.</summary>
    public async Task PlayFirstAsync()
    {
        if (Vm?.Games.Count > 0)
            await LaunchGameAsync(Vm.Games[0]);
    }

    /// <summary>
    /// Boot a game in the EmulatorWindow. Phase 3: downloads the core on first launch, resolves the
    /// gamebox, ensures a bundled CD mounts as D:, and runs on the software path. The smart
    /// executable picker, graduate-installed-game, and 3dfx hardware path are Phase 4 (backlog C/D);
    /// for now the gamebox's imported launch profile is used.
    /// </summary>
    private async Task LaunchGameAsync(GameTile tile, bool bootToDos = false)
    {
        if (Vm is null)
            return;
        var services = Services;
        try
        {

        // The core is downloaded on demand (never bundled), so fetch it on first launch.
        if (!services.Downloads.IsInstalled(AssetManifest.DosBoxPure))
        {
            Vm.Report("Downloading DOSBox Pure core…", busy: true);
            var dl = await services.Downloads.DownloadAsync(AssetManifest.DosBoxPure);
            if (!dl.Success)
            {
                Vm.Report($"Core download failed: {dl.Error}", busy: false);
                return;
            }
        }

        var instance = services.Store.Resolve(tile.Game.GameboxPath);

        // Ensure a folder game's bundled CD mounts as D: (covers games whose mount wasn't set up yet).
        var withDisc = Core.Import.ImportPipeline.EnsureBundledDiscMounted(instance.Profile, instance.ContentPath);
        if (!ReferenceEquals(withDisc, instance.Profile))
        {
            services.Store.WriteProfile(tile.Game.GameboxPath, withDisc);
            instance = instance with { Profile = withDisc };
        }

        if (bootToDos)
            instance = instance with { Profile = instance.Profile with { Launch = new LaunchSpec() } };

        // Folder games write in-game saves into content/; snapshot a baseline before play so the
        // Manage window and cloud sync can tell saves from the original game files.
        if (instance.Profile.SourceMedia != SourceMediaType.Iso)
            ContentBaseline.CaptureIfMissing(instance.ContentPath, instance.SavePath);

        var engine = new DosBoxPureEngine(
            services.Downloads.InstalledPath(AssetManifest.DosBoxPure), services.Paths.SystemDir, hardware3dfx: false);
        services.Library.RecordPlay(tile.Id);
        new EmulatorWindow(engine, instance, tile.Id).Show();
        Vm.ClearStatus();
        }
        catch (Exception ex)
        {
            services.SystemLog.Info($"Launch failed: {ex}");
            Vm.Report($"Couldn't launch: {ex.Message}", busy: false);
        }
    }
}

/// <summary>Minimal ICommand wrapper so menu items can run a plain Action. (CommunityToolkit's
/// RelayCommand needs a partial-method source generator; this keeps the inline menu wiring simple.)</summary>
internal sealed class RelayAction(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
