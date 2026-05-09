using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

(string Url, string Table)[] datasets =
{
    ("https://datasets.imdbws.com/name.basics.tsv.gz",      "name_basics"),
    ("https://datasets.imdbws.com/title.akas.tsv.gz",       "title_akas"),
    ("https://datasets.imdbws.com/title.basics.tsv.gz",     "title_basics"),
    ("https://datasets.imdbws.com/title.crew.tsv.gz",       "title_crew"),
    ("https://datasets.imdbws.com/title.episode.tsv.gz",    "title_episode"),
    ("https://datasets.imdbws.com/title.principals.tsv.gz", "title_principals"),
    ("https://datasets.imdbws.com/title.ratings.tsv.gz",    "title_ratings"),
};

string downloadFolder = Path.Combine(AppContext.BaseDirectory, "ImdbData");
Directory.CreateDirectory(downloadFolder);

IConfigurationRoot config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

string connString = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Connection string 'Postgres' not found in appsettings.json");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

Console.WriteLine("=== Download ===");
foreach ((string url, _) in datasets)
{
    string fileName = Path.GetFileName(new Uri(url).LocalPath);
    string localPath = Path.Combine(downloadFolder, fileName);
    string tempPath = localPath + ".part";

    if (File.Exists(localPath))
    {
        Console.WriteLine($"Skip   : {fileName} (already complete)");
        continue;
    }

    try
    {
        await DownloadAsync(http, url, fileName, localPath, tempPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Error  : {fileName} - {ex.Message}");
        Console.WriteLine($"         (.part preserved; rerun to resume)");
    }
}

Console.WriteLine();
Console.WriteLine("=== Import into PostgreSQL ===");

await using var conn = new NpgsqlConnection(connString);
await conn.OpenAsync();
Console.WriteLine($"Conn   : {conn.Host}:{conn.Port}/{conn.Database}");

await EnsureSchemaAsync(conn);
Console.WriteLine("Schema : ensured");

Dictionary<string, int[]> arrayColumnIndices = new()
{
    ["name_basics"]  = new[] { 4, 5 },
    ["title_basics"] = new[] { 8 },
    ["title_crew"]   = new[] { 1, 2 },
};

foreach ((string url, string table) in datasets)
{
    string fileName = Path.GetFileName(new Uri(url).LocalPath);
    string localPath = Path.Combine(downloadFolder, fileName);

    if (!File.Exists(localPath))
    {
        Console.WriteLine($"Skip   : {table} (file missing: {fileName})");
        continue;
    }

    int[] arrayCols = arrayColumnIndices.TryGetValue(table, out int[]? cols)
        ? cols
        : Array.Empty<int>();

    try
    {
        await ImportAsync(conn, localPath, table, arrayCols);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Error  : {table} - {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("Done.");

static async Task DownloadAsync(HttpClient http, string url, string fileName, string localPath, string tempPath)
{
    long resumeFrom = 0;
    long? remoteSize = null;

    if (File.Exists(tempPath))
    {
        long partSize = new FileInfo(tempPath).Length;

        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using HttpResponseMessage headResp = await http.SendAsync(head);
        headResp.EnsureSuccessStatusCode();
        remoteSize = headResp.Content.Headers.ContentLength;

        if (remoteSize is long total)
        {
            if (partSize == total)
            {
                File.Move(tempPath, localPath);
                Console.WriteLine($"Finish : {fileName} (existing .part was already complete)");
                return;
            }
            if (partSize > total)
            {
                Console.WriteLine($"Reset  : {fileName} (.part size {partSize} > remote {total}; discarding)");
                File.Delete(tempPath);
                resumeFrom = 0;
            }
            else
            {
                resumeFrom = partSize;
            }
        }
        else
        {
            resumeFrom = partSize;
        }
    }

    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    if (resumeFrom > 0)
    {
        request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
    }

    using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();

    bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
    if (resumeFrom > 0 && !resumed)
    {
        Console.WriteLine($"Reset  : {fileName} (server returned 200 instead of 206; restarting from 0)");
        resumeFrom = 0;
    }

    long? totalSize = resumed
        ? response.Content.Headers.ContentRange?.Length
        : (response.Content.Headers.ContentLength ?? remoteSize);

    if (resumed)
    {
        Console.WriteLine($"Resume : {fileName} from {resumeFrom / 1024.0 / 1024.0:F1} MB");
    }
    else
    {
        Console.WriteLine($"Get    : {fileName}");
    }

    FileMode mode = resumed ? FileMode.Append : FileMode.Create;
    await using (FileStream dst = new FileStream(tempPath, mode, FileAccess.Write, FileShare.None))
    await using (Stream src = await response.Content.ReadAsStreamAsync())
    {
        byte[] buffer = new byte[81920];
        long received = resumed ? resumeFrom : 0;
        DateTime lastReport = DateTime.UtcNow;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1)
            {
                WriteDownloadProgress(received, totalSize);
                lastReport = DateTime.UtcNow;
            }
        }
        WriteDownloadProgress(received, totalSize);
        Console.WriteLine();
    }

    File.Move(tempPath, localPath);
}

static void WriteDownloadProgress(long received, long? total)
{
    double mb = received / 1024.0 / 1024.0;
    if (total is long t && t > 0)
    {
        double totalMb = t / 1024.0 / 1024.0;
        double pct = (double)received / t * 100;
        Console.Write($"\r         {mb,8:F1} MB / {totalMb,8:F1} MB ({pct,5:F1}%)");
    }
    else
    {
        Console.Write($"\r         {mb,8:F1} MB");
    }
}

static async Task EnsureSchemaAsync(NpgsqlConnection conn)
{
    const string sql = """
        CREATE TABLE IF NOT EXISTS name_basics (
            nconst              TEXT,
            primary_name        TEXT,
            birth_year          INTEGER,
            death_year          INTEGER,
            primary_profession  TEXT[],
            known_for_titles    TEXT[]
        );

        CREATE TABLE IF NOT EXISTS title_akas (
            title_id            TEXT,
            ordering            INTEGER,
            title               TEXT,
            region              TEXT,
            language            TEXT,
            types               TEXT,
            attributes          TEXT,
            is_original_title   BOOLEAN
        );

        CREATE TABLE IF NOT EXISTS title_basics (
            tconst              TEXT,
            title_type          TEXT,
            primary_title       TEXT,
            original_title      TEXT,
            is_adult            BOOLEAN,
            start_year          INTEGER,
            end_year            INTEGER,
            runtime_minutes     INTEGER,
            genres              TEXT[]
        );

        CREATE TABLE IF NOT EXISTS title_crew (
            tconst              TEXT,
            directors           TEXT[],
            writers             TEXT[]
        );

        CREATE TABLE IF NOT EXISTS title_episode (
            tconst              TEXT,
            parent_tconst       TEXT,
            season_number       INTEGER,
            episode_number      INTEGER
        );

        CREATE TABLE IF NOT EXISTS title_principals (
            tconst              TEXT,
            ordering            INTEGER,
            nconst              TEXT,
            category            TEXT,
            job                 TEXT,
            characters          JSONB
        );

        CREATE TABLE IF NOT EXISTS title_ratings (
            tconst              TEXT,
            average_rating      NUMERIC(3,1),
            num_votes           INTEGER
        );
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

static string WrapArrayFields(string line, int[] indices)
{
    string[] fields = line.Split('\t');
    foreach (int idx in indices)
    {
        if (idx >= fields.Length) continue;
        string v = fields[idx];
        if (v.Length == 0)
        {
            fields[idx] = @"\N";
        }
        else if (v != @"\N")
        {
            fields[idx] = "{" + v + "}";
        }
    }
    return string.Join('\t', fields);
}

static async Task ImportAsync(NpgsqlConnection conn, string gzPath, string table, int[] arrayCols)
{
    await using (var probe = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {table})", conn))
    {
        bool hasRows = (bool)(await probe.ExecuteScalarAsync())!;
        if (hasRows)
        {
            Console.WriteLine($"Skip   : {table} (already populated; DROP TABLE {table}; to refresh)");
            return;
        }
    }

    DateTime start = DateTime.UtcNow;
    DateTime lastReport = DateTime.UtcNow;
    long rows = 0;

    Console.Write($"Load   : {table} ... ");

    await using FileStream fs = File.OpenRead(gzPath);
    await using var gz = new GZipStream(fs, CompressionMode.Decompress);
    using var reader = new StreamReader(gz, Encoding.UTF8);

    await reader.ReadLineAsync();

    await using (TextWriter writer = await conn.BeginTextImportAsync($"COPY {table} FROM STDIN"))
    {
        writer.NewLine = "\n";
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (arrayCols.Length > 0)
            {
                line = WrapArrayFields(line, arrayCols);
            }
            await writer.WriteLineAsync(line);
            rows++;
            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
            {
                Console.Write($"\rLoad   : {table} ... {rows:N0} rows");
                lastReport = DateTime.UtcNow;
            }
        }
    }

    TimeSpan elapsed = DateTime.UtcNow - start;
    Console.WriteLine($"\rLoad   : {table} ... {rows:N0} rows in {elapsed.TotalSeconds:F1}s              ");
}
