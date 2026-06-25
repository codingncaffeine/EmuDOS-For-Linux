using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using EmuDOS.Core.Library;
using EmuDOS.Core.Model;
using EmuDOS.Services;
using EmuDOS.ViewModels;

namespace EmuDOS.Views;

/// <summary>
/// The per-game detail card: a floating card over the shelf (no dimming, stays on top of the app)
/// with a 4:3 art/video-snap banner, meta and activity pills, and Play / Favorite / "…" actions.
/// Imperative (Populate-style), modelled on the Windows card and themed with EmuDOS's tokens. The
/// video snap reuses <see cref="SnapPlayer"/> (the WPF original inlined its own VLC player).
/// </summary>
public partial class GameDetailWindow : Window
{
    private readonly GameTile _tile;
    private readonly AppServices _services;
    private readonly Action _onPlay;
    private readonly IReadOnlyList<(string Label, Action Run)> _overflow;
    private bool _isFavorite;

    public GameDetailWindow(GameTile tile, AppServices services, Action onPlay,
                            IReadOnlyList<(string Label, Action Run)> overflow)
    {
        InitializeComponent();
        _tile = tile;
        _services = services;
        _onPlay = onPlay;
        _overflow = overflow;
        Populate();
    }

    private void Populate()
    {
        ArtPlaceholderText.Text = _tile.Title;
        GameTitle.Text = _tile.Title;
        MachineTag.Text = "DOS";

        if (_tile.Cover is { } cover)
        {
            HeaderImage.Source = cover;
            HeaderImage.IsVisible = true;
            ArtPlaceholderText.IsVisible = false;
        }

        var g = _services.Library.GetGame(_tile.Id) ?? _tile.Game; // fresh stats
        _isFavorite = g.IsFavorite;
        UpdateFavorite();
        PopulateStats(g);

        if (_services.Store.ReadMetadata(_tile.Game.GameboxPath) is { } md)
            PopulateMetadata(md);
        else
            _ = LoadMetadataAsync();

        _ = LoadSnapAsync();
    }

    // Fetch descriptive metadata on demand (for games imported before metadata existed) and show it
    // under the title; stored as the gamebox metadata.json + cached so it survives delete/re-import.
    private async Task LoadMetadataAsync()
    {
        try
        {
            var md = await _services.Art.FetchMetadataAsync(_tile.Title);
            if (md is null || _closed)
                return;
            var root = _tile.Game.GameboxPath;
            _services.Store.WriteMetadata(root, md);
            _services.ArtCache.StashMetadata(_tile.Title, Path.Combine(root, "metadata.json"));
            PopulateMetadata(md);
        }
        catch { /* leave the meta pills empty */ }
    }

    private void PopulateStats(LibraryGame g)
    {
        StatPlayed.Text = g.PlayCount == 0
            ? "Never played"
            : $"{g.PlayCount} play{(g.PlayCount == 1 ? "" : "s")}";
        if (g.TotalPlayTimeSeconds > 0)
        {
            StatPlayTime.Text = FormatDuration(g.TotalPlayTimeSeconds);
            PlayTimePill.IsVisible = true;
        }
    }

    private void PopulateMetadata(GameMetadata md)
    {
        SetPill(YearPill, GameYear, md.Year);
        SetPill(DeveloperPill, GameDeveloper, string.IsNullOrWhiteSpace(md.Developer) ? md.Publisher : md.Developer);
        SetPill(GenrePill, GameGenre, md.Genre);

        if (!string.IsNullOrWhiteSpace(md.Description))
        {
            GameDescription.Text = md.Description;
            GameDescriptionScroll.IsVisible = true;
        }
    }

    private static void SetPill(Border pill, TextBlock text, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        text.Text = value;
        ToolTip.SetTip(text, value);
        pill.IsVisible = true;
    }

    public static string FormatDuration(long seconds) => seconds switch
    {
        < 60 => $"{seconds}s",
        < 3600 => $"{seconds / 60}m",
        < 360000 => $"{seconds / 3600.0:0.0}h",
        _ => $"{seconds / 3600}h",
    };

    private void UpdateFavorite()
    {
        FavoriteButton.Content = _isFavorite ? "♥  Favorited" : "♡  Favorite";
        FavoriteButton.Foreground = _isFavorite ? Brush("Accent") : Brush("TextPrimary");
        FavoriteBadge.IsVisible = _isFavorite;
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, out var v) ? v as IBrush : null;

    private void OnFavorite(object? sender, RoutedEventArgs e)
    {
        _isFavorite = !_isFavorite;
        _services.Library.SetFavorite(_tile.Id, _isFavorite);
        _tile.IsFavorite = _isFavorite; // live-updates the shelf heart badge
        UpdateFavorite();
    }

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        Close();
        _onPlay();
    }

    private void OnMore(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.Top };
        foreach (var (label, run) in _overflow)
        {
            var captured = run;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { Close(); captured(); };
            flyout.Items.Add(item);
        }
        if (sender is Control c)
            flyout.ShowAt(c);
    }

    private void OnClose(object? sender, PointerPressedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    // ── Video snap (ScreenScraper, cached in the retained Snaps folder; placeholder → crossfade) ──
    private SnapPlayer? _snap;
    private bool _closed, _crossfadeDone;

    private async Task LoadSnapAsync()
    {
        try
        {
            var snapPath = Path.Combine(_services.Paths.SnapsDir, SnapKey() + ".mp4");
            if (!File.Exists(snapPath))
            {
                if (!await _services.Art.FetchSnapAsync(_tile.Title, snapPath) || _closed)
                    return;
            }
            if (_closed || !File.Exists(snapPath))
                return;

            _snap = new SnapPlayer(Dispatcher.UIThread);
            VideoImage.Source = _snap.Bitmap;
            _snap.FrameDrawn += () => VideoImage.InvalidateVisual();
            _snap.Play(snapPath, onFirstFrame: RevealVideo);
        }
        catch { /* no snap — the banner keeps showing the cover art */ }
    }

    // Reveal the video and fade the cover/placeholder out over the HeaderImage opacity transition.
    private void RevealVideo()
    {
        if (_closed || _crossfadeDone)
            return;
        _crossfadeDone = true;
        VideoImage.IsVisible = true;
        HeaderImage.Opacity = 0;
    }

    private string SnapKey()
    {
        var id = string.IsNullOrWhiteSpace(_tile.Game.CanonicalId) ? _tile.Title : _tile.Game.CanonicalId!;
        return string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        var snap = _snap;
        _snap = null;
        Task.Run(() => { try { snap?.Dispose(); } catch { } });
        base.OnClosed(e);
    }
}
