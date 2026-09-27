using EmuDOS.Core.Downloads;
using Microsoft.Data.Sqlite;

namespace EmuDOS.Core.Catalog;

/// <summary>Outcome of a catalog refresh.</summary>
/// <param name="Installed">A newer catalog replaced the installed one.</param>
/// <param name="Revision">The installed catalog's revision afterwards.</param>
/// <param name="Entries">The installed catalog's game count afterwards.</param>
/// <param name="Error">Why the refresh failed, or null.</param>
public sealed record CatalogUpdateResult(bool Installed, int Revision, int Entries, string? Error);

/// <summary>
/// Keeps the installed catalog at the newest copy available: the baseline embedded in the app (so a
/// first run recognises games before anything is downloaded) or the latest release's
/// <c>catalog.db</c>. A candidate replaces the installed catalog only when it is a catalog of this
/// format with entries and a higher revision, so a failed or stale download never downgrades it.
/// The copy goes through SQLite's online backup, which is safe while the app has the catalog open.
/// </summary>
public sealed class CatalogUpdater(CatalogDatabase catalog, DownloadService downloads)
{
    /// <summary>Manifest name of the baseline catalog embedded in EmuDOS.Core.</summary>
    public const string BaselineResource = "EmuDOS.Catalog.baseline.db";

    /// <summary>Install the embedded baseline when it is newer than the installed catalog.
    /// Returns true when it installed. File I/O: call it off the UI thread.</summary>
    public bool EnsureBaseline()
    {
        using var stream = typeof(CatalogUpdater).Assembly.GetManifestResourceStream(BaselineResource);
        if (stream is null)
            return false;
        var temp = catalog.Path + ".baseline.tmp";
        try
        {
            using (var file = File.Create(temp))
                stream.CopyTo(file);
            return InstallIfNewer(temp, catalog.Path);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Fetch the latest release's catalog and install it when it is newer.</summary>
    public async Task<CatalogUpdateResult> UpdateAsync(
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var temp = catalog.Path + ".download.tmp";
        try
        {
            await downloads.FetchAsync(AssetManifest.Catalog.Url, temp, progress, cancellationToken)
                .ConfigureAwait(false);
            var installed = InstallIfNewer(temp, catalog.Path);
            return new CatalogUpdateResult(installed, catalog.Revision, catalog.Count, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or SqliteException or UnauthorizedAccessException)
        {
            return new CatalogUpdateResult(false, SafeRevision(), SafeCount(), ex.Message);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Copy <paramref name="candidatePath"/> over <paramref name="installedPath"/> when the
    /// candidate is a non-empty catalog of this format with a higher revision (or the installed one is
    /// missing, empty or of another format). Returns true when it copied.</summary>
    public static bool InstallIfNewer(string candidatePath, string installedPath)
    {
        var candidate = CatalogDatabase.ReadInfo(candidatePath);
        if (candidate is null || candidate.Format != CatalogDatabase.FormatVersion || candidate.Entries == 0)
            return false;

        var installed = CatalogDatabase.ReadInfo(installedPath);
        if (installed is not null
            && installed.Format == CatalogDatabase.FormatVersion
            && installed.Entries > 0
            && installed.Revision >= candidate.Revision)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(installedPath))!);
        using var source = OpenUnpooled(candidatePath, SqliteOpenMode.ReadOnly);
        using var target = OpenUnpooled(installedPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(target);
        return true;
    }

    private static SqliteConnection OpenUnpooled(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private int SafeRevision()
    {
        try { return catalog.Revision; }
        catch (SqliteException) { return 0; }
    }

    private int SafeCount()
    {
        try { return catalog.Count; }
        catch (SqliteException) { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
