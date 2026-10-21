using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RASv2.ClashAnalysis.Site.Data;

/// <summary>
/// Копии базы — архивом zip с одним файлом «clashes.db» внутри: база сжимается в 7 раз (DECISIONS.md модуля, «Копии базы —
/// раз в день, лесенкой»). Восстановить — распаковать «clashes.db» на место базы при остановленном сайте (MODULE.md, «Копии базы»).
/// </summary>
public static class DatabaseCopy
{
    /// <summary>Имя базы внутри архива.</summary>
    public const string Inside = "clashes.db";
    /// <summary>Папка копий перед изменениями (перенос базы, удаление проекта, обновление сайта) — внутри «Копий базы» этого компьютера.</summary>
    public const string BeforeChangesFolder = "Перед изменениями";
    public const int BeforeChangesKeep = 10;

    /// <summary>Цельная копия базы средствами SQLite (даже если в этот момент идёт запись) — во временный файл в folder; удаляет вызывающий.</summary>
    public static string Snapshot(SqliteConnection source, string folder)
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $"~копия-{Guid.NewGuid():N}.db");
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }
        return temp;
    }

    /// <summary>Файл базы — в архив; через временный файл: недописанный архив не выглядит целым.</summary>
    public static void Zip(string databaseFile, string zipFile)
    {
        var temp = zipFile + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(databaseFile, Inside, CompressionLevel.Optimal);
        File.Move(temp, zipFile, overwrite: true);
    }

    /// <summary>Отпечаток файла: одинаковая база даёт одинаковую копию SQLite — по нему видно, менялась ли база.</summary>
    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Копия перед изменением, которое не отменить (перенос базы на новую версию, удаление проекта): «Копии базы\Перед
    /// изменениями\ГГГГ-ММ-ДД ЧЧММСС что.zip» рядом с базой; хранятся последние 10. Возвращает путь архива.
    /// </summary>
    public static string BeforeChange(SqliteConnection source, string databaseFile, string what)
    {
        var folder = BeforeChangesOf(databaseFile);
        var snapshot = Snapshot(source, folder);
        try
        {
            var zip = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd HHmmss} {SafeName(what)}.zip");
            Zip(snapshot, zip);
            PruneBeforeChanges(folder);
            return zip;
        }
        finally
        {
            File.Delete(snapshot);
        }
    }

    public static string BeforeChangesOf(string databaseFile) =>
        Path.Combine(Path.GetDirectoryName(databaseFile)!, "Копии базы", BeforeChangesFolder);

    /// <summary>Оставить последние 10 (имя начинается с даты и времени — порядок по имени).</summary>
    public static void PruneBeforeChanges(string folder)
    {
        foreach (var old in Directory.GetFiles(folder, "*.zip").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(BeforeChangesKeep))
            File.Delete(old);
    }

    /// <summary>Для имени файла: без запрещённых знаков, не длиннее 80.</summary>
    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return safe.Length > 80 ? safe[..80] : safe;
    }
}
