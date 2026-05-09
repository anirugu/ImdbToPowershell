using System.Net;
using System.Net.Http.Headers;

string[] urls =
{
    "https://datasets.imdbws.com/name.basics.tsv.gz",
    "https://datasets.imdbws.com/title.akas.tsv.gz",
    "https://datasets.imdbws.com/title.basics.tsv.gz",
    "https://datasets.imdbws.com/title.crew.tsv.gz",
    "https://datasets.imdbws.com/title.episode.tsv.gz",
    "https://datasets.imdbws.com/title.principals.tsv.gz",
    "https://datasets.imdbws.com/title.ratings.tsv.gz",
};

string downloadFolder = Path.Combine(AppContext.BaseDirectory, "ImdbData");
Directory.CreateDirectory(downloadFolder);

using var http = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(30),
};

foreach (string url in urls)
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
                WriteProgress(received, totalSize);
                lastReport = DateTime.UtcNow;
            }
        }
        WriteProgress(received, totalSize);
        Console.WriteLine();
    }

    File.Move(tempPath, localPath);
}

static void WriteProgress(long received, long? total)
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
