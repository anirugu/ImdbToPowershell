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

    if (File.Exists(localPath))
    {
        Console.WriteLine($"Skip  : {fileName} (already exists)");
        continue;
    }

    Console.WriteLine($"Get   : {fileName}");
    string tempPath = localPath + ".part";

    try
    {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using (Stream src = await response.Content.ReadAsStreamAsync())
        await using (FileStream dst = File.Create(tempPath))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            DateTime lastReport = DateTime.UtcNow;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                received += read;

                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 1)
                {
                    WriteProgress(received, totalBytes);
                    lastReport = DateTime.UtcNow;
                }
            }
            WriteProgress(received, totalBytes);
            Console.WriteLine();
        }

        File.Move(tempPath, localPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine();
        Console.WriteLine($"Error : {fileName} - {ex.Message}");
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}

Console.WriteLine("Done.");

static void WriteProgress(long received, long? total)
{
    double mb = received / 1024.0 / 1024.0;
    if (total is long t && t > 0)
    {
        double totalMb = t / 1024.0 / 1024.0;
        double pct = (double)received / t * 100;
        Console.Write($"\r        {mb,8:F1} MB / {totalMb,8:F1} MB ({pct,5:F1}%)");
    }
    else
    {
        Console.Write($"\r        {mb,8:F1} MB");
    }
}
