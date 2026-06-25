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
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    // ── Hover video preview (Trinitron monitor popup) ────────────────────────────────────────
    private SnapPlayer? _hoverPlayer;
    private DispatcherTimer? _hoverTimer;
    private GameTile? _hoverTile;
    private Control? _hoverElement;
    private readonly HashSet<long> _noSnap = [];

    private void OnBoxPointerEntered(object? sender, PointerEventArgs e)
    {
        if (Vm is null || Vm.IsEditMode || _openCard is not null)
            return;
        if (sender is not Control { DataContext: GameTile tile } element)
            return;
        _hoverTile = tile;
        _hoverElement = element;
        _hoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _hoverTimer.Tick -= OnHoverTick;
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnBoxPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: GameTile tile } && tile == _hoverTile)
        {
            _hoverTimer?.Stop();
            _hoverTile = null;
            HideHoverPreview();
        }
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        if (_hoverTile is { } tile && _hoverElement is { } element)
            ShowHoverPreview(tile, element);
    }

    private async void ShowHoverPreview(GameTile tile, Control element)
    {
        if (_noSnap.Contains(tile.Id) || _openCard is not null)
            return;
        var services = Services;
        var snapPath = Path.Combine(services.Paths.SnapsDir, SnapKeyFor(tile) + ".mp4");
        if (!File.Exists(snapPath))
        {
            // Fetch once in the background; the preview shows next hover (don't block or spam SS).
            try { await services.Art.FetchSnapAsync(tile.Title, snapPath); } catch { }
            if (!File.Exists(snapPath))
                _noSnap.Add(tile.Id);
            return;
        }
        if (_hoverTile != tile)
            return;

        if (_hoverPlayer is null)
        {
            _hoverPlayer = new SnapPlayer(Dispatcher.UIThread);
            _hoverPlayer.FrameDrawn += () => HoverVideo.InvalidateVisual();
        }
        HoverVideo.Source = _hoverPlayer.Bitmap;
        HoverPopup.PlacementTarget = element;
        HoverPopup.Placement = PlacementMode.Right;
        _hoverPlayer.Play(snapPath, onFirstFrame: () => { if (_hoverTile == tile) HoverPopup.IsOpen = true; });
    }

    private void HideHoverPreview()
    {
        HoverPopup.IsOpen = false;
        _hoverPlayer?.Stop();
    }

    private static string SnapKeyFor(GameTile tile)
    {
        var id = string.IsNullOrWhiteSpace(tile.Game.CanonicalId) ? tile.Title : tile.Game.CanonicalId!;
        return string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
    }

    // ── The per-game detail card ─────────────────────────────────────────────────────────────
    private GameDetailWindow? _openCard;

    /// <summary>Open the per-game detail card (Play launches; ★ favorites; "…" has the rest). The
    /// overflow actions that need a not-yet-ported window route to a status note until that window
    /// lands (tracked in notes/PHASE4-BACKLOG.md).</summary>
    private void OpenGameCard(GameTile tile)
    {
        _openCard?.Close();
        _hoverTimer?.Stop();
        HideHoverPreview(); // a card takes over — no hover monitor behind it
        var services = Services;

        var overflow = new List<(string, Action)>
        {
            ("Manage…", () => ComingSoon("Manage window")),
            ("Rename from ScreenScraper…", () => RenameFromScreenScraper(tile)),
            ("Cheats… (preview)", () => ComingSoon("Cheats")),
            ("Game preferences…", () => ComingSoon("Game preferences")),
            ("Open in DOS", () => _ = LaunchGameAsync(tile, bootToDos: true)),
            ("Launch parameters…", () => EditLaunchParameters(tile)),
            ("Read manual", () => _ = OpenManualAsync(tile)),
        };

        var executables = BuildExecutableList(tile);
        if (executables.Count > 0)
            overflow.Add(("Choose program…", () => ChooseProgram(tile, executables)));

        _openCard = new GameDetailWindow(tile, services, () => _ = LaunchGameAsync(tile), overflow);
        _openCard.Closed += (s, _) => { if (ReferenceEquals(_openCard, s)) _openCard = null; };
        _openCard.Show(this);
    }

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
        var exeList = BuildExecutableList(tile);
        if (exeList.Count > 0)
            menu.Items.Add(Item("🎮  Choose program…", () => ChooseProgram(tile, exeList)));

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
        menu.Items.Add(Item("✏  Rename from ScreenScraper…", () => RenameFromScreenScraper(tile)));
        menu.Items.Add(Item("📖  Read manual", () => _ = OpenManualAsync(tile)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("🗑  Delete", () => DeleteGamesConfirmed(new[] { tile })));

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

    // ── Manual / rename / launch parameters ──────────────────────────────────────────────────

    /// <summary>Open the game's manual. Downloads it on first use; then shell-opens the system PDF
    /// handler (WebView2 has no Linux equivalent — that's the locked porting decision).</summary>
    private async Task OpenManualAsync(GameTile tile)
    {
        if (Vm is null)
            return;
        var services = Services;
        var manual = FindManual(tile, services);
        if (manual is null)
        {
            Vm.Report($"Downloading manual for {tile.Title}…", busy: true);
            try
            {
                var dir = Path.Combine(services.Paths.ManualsDir, SanitizeName(tile.Title));
                manual = await services.Manuals.FetchManualAsync(tile.Title, dir);
            }
            catch (Exception ex) { Vm.Report($"Manual download failed: {ex.Message}", busy: false); return; }
            if (manual is null) { Vm.Report($"No manual found for {tile.Title}.", busy: false); return; }
            Vm.ClearStatus();
        }
        try { Process.Start(new ProcessStartInfo(manual) { UseShellExecute = true }); } // xdg-open
        catch (Exception ex) { Vm.Report($"Couldn't open the manual: {ex.Message}", busy: false); }
    }

    // An already-downloaded manual, or one bundled with the game (eXoDOS ships PDFs in the content).
    private static string? FindManual(GameTile tile, AppServices services)
    {
        var dir = Path.Combine(services.Paths.ManualsDir, SanitizeName(tile.Title));
        if (Directory.Exists(dir))
        {
            var downloaded = Directory.EnumerateFiles(dir, "manual.*").FirstOrDefault();
            if (downloaded is not null)
                return downloaded;
        }
        var content = Path.Combine(tile.Game.GameboxPath, "content");
        return Directory.Exists(content)
            ? Directory.EnumerateFiles(content, "*.pdf", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private async void RenameFromScreenScraper(GameTile tile)
    {
        var value = await TextPromptDialog.ShowAsync(this,
            $"Rename from ScreenScraper — {tile.Title}",
            "Type the game's name to look up. It'll be renamed to ScreenScraper's matched title and its " +
            "art and details refreshed. Use the exact title (e.g. \"King's Quest VI\") for ones the " +
            "automatic match can't find.",
            tile.Title);
        if (string.IsNullOrWhiteSpace(value) || Vm is null)
            return;
        await Vm.RenameFromScreenScraperAsync(tile, value.Trim());
    }

    private async void EditLaunchParameters(GameTile tile)
    {
        var services = Services;
        var profile = services.Store.ReadProfile(tile.Game.GameboxPath);
        var value = await TextPromptDialog.ShowAsync(this,
            $"Launch parameters — {tile.Title}",
            "Command-line arguments passed to the game's program when it starts (e.g. a sound-mode switch some games need). Leave blank for none.",
            profile.Launch.Arguments);
        if (value is null)
            return; // cancelled
        var args = string.IsNullOrWhiteSpace(value) ? null : value;
        var updated = profile with { Launch = profile.Launch with { Arguments = args } };
        services.Store.WriteProfile(tile.Game.GameboxPath, updated);
        Vm?.Report(
            args is null ? $"Cleared launch parameters for {tile.Title}." : $"Launch parameters set: {args}",
            busy: false);
    }

    // ── Smart executable picker (Choose program…) ────────────────────────────────────────────

    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat"];

    /// <summary>The programs offered in the picker: ones the user knows about + a content scan +
    /// anything installed on the persisted C: drive, with 3dfx builds floated to the top.</summary>
    private List<string> BuildExecutableList(GameTile tile)
    {
        var executables = OrderedExecutables(
            Services.Store.ReadState(tile.Game.GameboxPath),
            ScanExecutables(Path.Combine(tile.Game.GameboxPath, "content")));
        foreach (var installed in PureSave.ListInstalledExecutables(Path.Combine(tile.Game.GameboxPath, "saves")))
            if (!executables.Contains(installed, StringComparer.OrdinalIgnoreCase))
                executables.Add(installed);
        return executables;
    }

    private async void ChooseProgram(GameTile tile, List<string> executables)
    {
        var services = Services;
        var state = services.Store.ReadState(tile.Game.GameboxPath);
        // Pre-select what we'd launch by default: the remembered exe, else the smart-detected game.
        var current = string.IsNullOrWhiteSpace(state.LastExecutable)
            ? BestGameExecutable(Path.Combine(tile.Game.GameboxPath, "content"), tile.Title)
            : state.LastExecutable;

        var exe = await ChooseProgramDialog.ShowAsync(this, tile.Title, executables, current);
        if (exe is null)
            return;

        if (PureSave.IsInstalledPath(exe))
        {
            // A program installed on the persisted C: drive — pin it via AUTOBOOT.DBP so dosbox_pure
            // boots straight into it with the disc still mounted as D: (CD checks / audio keep working).
            PureSave.SetAutoBoot(Path.Combine(tile.Game.GameboxPath, "saves"), exe);
            await LaunchGameAsync(tile);
        }
        else
        {
            await LaunchGameAsync(tile, executableOverride: exe); // remembers it (unless it's a setup tool)
        }
    }

    /// <summary>A configuration/installer program, not the game — shouldn't become the default launch.</summary>
    private static bool IsSetupLike(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name.Contains("setup") || name.Contains("install") || name.Contains("config");
    }

    /// <summary>DOS-relative paths of runnable files under the content (minus the DOSBox wrapper).</summary>
    private static List<string> ScanExecutables(string contentDir)
    {
        var found = new List<string>();
        if (!Directory.Exists(contentDir))
            return found;
        try
        {
            foreach (var file in Directory.EnumerateFiles(contentDir, "*.*", SearchOption.AllDirectories))
            {
                if (!ExecutableExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;
                if (Core.Import.DosExecutables.IsRuntimeHelper(file))
                    continue; // DOS extenders / the DOSBox wrapper aren't launch targets
                found.Add(Path.GetRelativePath(contentDir, file).Replace('/', '\\'));
            }
        }
        catch { /* best-effort scan */ }
        return found;
    }

    /// <summary>The most likely game program among the content's executables — a name matching the
    /// title, then a known launcher, then the largest exe — skipping installers and setup tools.</summary>
    private static string? BestGameExecutable(string contentDir, string title)
    {
        var pick = BestGameExecutableCore(contentDir, title);
        return pick is null ? null : Core.Import.DosExecutables.ResolveBatRedirect(contentDir, pick);
    }

    private static string? BestGameExecutableCore(string contentDir, string title)
    {
        var candidates = ScanExecutables(contentDir)
            .Where(e => !IsSetupLike(e) && !Core.Import.DosExecutables.IsRuntimeHelper(e))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var titled = candidates.FirstOrDefault(e => Core.Import.DosExecutables.TitleMatches(e, title));
        if (titled is not null)
            return titled;

        var known = candidates.FirstOrDefault(Core.Import.DosExecutables.IsKnownLauncher);
        if (known is not null)
            return known;

        static long Size(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }
        var best = candidates
            .Select(e => (exe: e, size: Size(Path.Combine(contentDir, e)), util: Core.Import.DosExecutables.IsLikelyUtility(e)))
            .OrderBy(x => x.util)
            .ThenByDescending(x => x.size)
            .FirstOrDefault();
        return best.size > 0 ? best.exe : (candidates.FirstOrDefault(e => e.Contains('\\')) ?? candidates[0]);
    }

    private static List<string> OrderedExecutables(GameUserState state, List<string> scanned)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var exe in state.KnownExecutables.Concat(scanned))
            if (!string.IsNullOrWhiteSpace(exe) && seen.Add(exe))
                ordered.Add(exe);
        // Float 3dfx/Glide executables to the top (stable sort keeps everything else in order).
        return ordered.OrderByDescending(Is3dfxExecutable).ToList();
    }

    /// <summary>Heuristic: an executable likely built to use a 3dfx/Voodoo (Glide) card.</summary>
    internal static bool Is3dfxExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name.Contains("3dfx") || name.Contains("glide") || name.Contains("voodoo")
            || name.StartsWith("gl") || name.EndsWith("gl");
    }

    private void DeleteSelected() =>
        DeleteGamesConfirmed(Vm?.Games.Where(g => g.IsSelected).ToList() ?? []);

    /// <summary>Confirm, then delete. Box art is preserved on delete, so re-importing restores covers
    /// without a re-download.</summary>
    private async void DeleteGamesConfirmed(IReadOnlyList<GameTile> games)
    {
        if (games.Count == 0)
            return;
        var names = games.Count <= 3
            ? string.Join(", ", games.Select(g => g.Title))
            : $"{games.Count} games";
        if (await ConfirmDialog.ShowAsync(this, "Delete from library",
                $"Remove {names} from your library?\n\nThe box art is kept, so re-importing won't re-download it.",
                "Delete"))
            Vm!.DeleteGames(games);
    }

    // ── Drag-and-drop: a single image onto a box sets its cover; anything else imports ──────────
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    private static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null || e.DataTransfer?.Contains(DataFormat.File) != true)
            return;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null)
            return;
        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToArray();
        if (paths.Length == 0)
            return;
        e.Handled = true;

        // A single image dropped onto a box sets that box's cover (browser-dragged image URLs are a
        // gap until the Avalonia DataTransfer text/URL formats are wired — notes/PHASE4-BACKLOG.md §A).
        if (paths.Length == 1 && IsImageFile(paths[0]) && TileAt(e.GetPosition(this)) is { } tile)
        {
            Vm.Report($"Adding box art for {tile.Title}…", busy: true);
            try { Vm.SetBoxArt(tile, await File.ReadAllBytesAsync(paths[0])); }
            catch { Vm.Report("Couldn't read the dropped image.", busy: false); }
            return;
        }

        await Vm.HandleDropAsync(paths);
    }

    // The game tile under a point (drop target), or null over empty shelf.
    private GameTile? TileAt(Point p)
    {
        var hit = this.InputHitTest(p) as Visual;
        while (hit is not null)
        {
            if (hit is Control { DataContext: GameTile t })
                return t;
            hit = hit.GetVisualParent();
        }
        return null;
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

    /// <summary>Open the detail card for the first game — used by the EMUDOS_AUTOCARD smoke hook.</summary>
    public void OpenFirstCard()
    {
        if (Vm?.Games.Count > 0)
            OpenGameCard(Vm.Games[0]);
    }

    /// <summary>
    /// Boot a game in the EmulatorWindow. Phase 3: downloads the core on first launch, resolves the
    /// gamebox, ensures a bundled CD mounts as D:, and runs on the software path. The smart
    /// executable picker, graduate-installed-game, and 3dfx hardware path are Phase 4 (backlog C/D);
    /// for now the gamebox's imported launch profile is used.
    /// </summary>
    private async Task LaunchGameAsync(GameTile tile, bool bootToDos = false, string? executableOverride = null,
                                       byte[]? loadState = null)
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

        // One-time: a CD game whose whole install ended up in the writable overlay (.pure.zip) gets
        // graduated to a folder layout, so tiny config saves write in place instead of rewriting the
        // entire multi-hundred-MB overlay each time (the ~1s save hitch + cloud-sync bloat).
        if (instance.Profile.SourceMedia == SourceMediaType.Iso)
        {
            Vm.Report($"Optimizing {tile.Title} for faster saves…", busy: true);
            var graduated = await Task.Run(() => PureSave.GraduateInstalledGame(tile.Game.GameboxPath, instance.Profile));
            if (graduated is not null)
            {
                services.Store.WriteProfile(tile.Game.GameboxPath, graduated);
                if (!string.IsNullOrWhiteSpace(graduated.Launch.Executable))
                    services.Store.WriteState(tile.Game.GameboxPath,
                        services.Store.ReadState(tile.Game.GameboxPath).WithExecutable(graduated.Launch.Executable!));
                instance = instance with { Profile = graduated };
                Vm.Report($"Optimized {tile.Title} for faster saves.", busy: false);
            }
        }

        // Ensure a folder game's bundled CD mounts as D: (covers freshly-graduated disc games and any
        // folder game whose mount wasn't set up yet). Persist if it changed so it's a one-time cost.
        var withDisc = Core.Import.ImportPipeline.EnsureBundledDiscMounted(instance.Profile, instance.ContentPath);
        if (!ReferenceEquals(withDisc, instance.Profile))
        {
            services.Store.WriteProfile(tile.Game.GameboxPath, withDisc);
            instance = instance with { Profile = withDisc };
        }

        if (bootToDos)
        {
            // No game launch — configure + drop to the C: prompt so the user can run the game's SETUP.
            instance = instance with { Profile = instance.Profile with { Launch = new LaunchSpec() } };
        }
        else
        {
            // Pick the executable. Order: an explicit Run/picker choice this launch, then a program the
            // user deliberately picked before, then the last program run from DOS, then auto-detect
            // (title / extender-launcher .bat / largest exe), then the configured guess.
            var state = services.Store.ReadState(tile.Game.GameboxPath);
            var configured = instance.Profile.Launch.Executable;
            var chosen = executableOverride
                ?? (state.ExecutableIsUserChoice ? state.LastExecutable : null)
                ?? state.LastRunProgram
                ?? BestGameExecutable(Path.Combine(tile.Game.GameboxPath, "content"), tile.Title)
                ?? configured;

            if (!string.Equals(chosen, configured, StringComparison.OrdinalIgnoreCase))
                instance = instance with
                {
                    Profile = instance.Profile with { Launch = instance.Profile.Launch with { Executable = chosen } },
                };

            // Only an explicit Run-menu pick becomes the new default — and not a one-off setup program.
            if (executableOverride is not null && !IsSetupLike(executableOverride))
                services.Store.WriteState(tile.Game.GameboxPath, state.WithExecutable(executableOverride));
        }

        // Folder games write in-game saves into content/; snapshot a baseline before play so the
        // Manage window and cloud sync can tell saves from the original game files.
        if (instance.Profile.SourceMedia != SourceMediaType.Iso)
            ContentBaseline.CaptureIfMissing(instance.ContentPath, instance.SavePath);

        // 3dfx hardware rendering stays software for now — the offscreen EGL path is deferred
        // (backlog C). The per-game/global 3dfx selection lights up once that lands.
        var engine = new DosBoxPureEngine(
            services.Downloads.InstalledPath(AssetManifest.DosBoxPure), services.Paths.SystemDir, hardware3dfx: false);
        services.Library.RecordPlay(tile.Id);
        new EmulatorWindow(engine, instance, tile.Id, loadState).Show();
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
