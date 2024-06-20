using PuppeteerSharp;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;

namespace AutoPlaylistFolder
{
    class Program
    {
        static Dictionary<string, string> downloadedFingerprints;
        static string jsonFile = "downloaded_videos.json";
        static ConcurrentQueue<(string webMFilePath, string mp4FilePath, string videoId)> conversionQueue = new ConcurrentQueue<(string, string, string)>();
        static SemaphoreSlim semaphore = new SemaphoreSlim(4); // Limit to 4 concurrent conversions
        static bool downloadingCompleted = false;

        public static async Task Main(string[] args)
        {
            InitializeDownloadedVideos();

            string playlistUrl = GetPlaylistUrl();
            string baseDirectory = GetBaseDirectory();

            await new BrowserFetcher().DownloadAsync();

            var browserOptions = new LaunchOptions { Headless = true };
            using (var browser = await Puppeteer.LaunchAsync(browserOptions))
            using (var page = await browser.NewPageAsync())
            {
                await page.GoToAsync(playlistUrl);
                var playlistTitle = await GetPlaylistTitle(page);
                string downloadPath = Path.Combine(baseDirectory, playlistTitle);

                Console.WriteLine($"Playlist: {playlistTitle}");
                Directory.CreateDirectory(downloadPath);

                await ScrollToLoadAllVideos(page);
                var videoLinks = await GetVideoLinks(page);

                Console.WriteLine($"Found {videoLinks.Length} videos in the playlist.");

                var downloadBlock = new ActionBlock<string>(
                    async link => await DownloadAndQueueConversion(link, downloadPath),
                    new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 4 });

                var conversionTask = ProcessConversionQueue();

                foreach (var link in videoLinks)
                {
                    await downloadBlock.SendAsync(link);
                }

                downloadBlock.Complete();
                await downloadBlock.Completion;

                downloadingCompleted = true; // Signal that downloading is completed
                await conversionTask;

                CleanupFiles(downloadPath);
                //ask if they'd like to check for missing songs
                Console.WriteLine("Would you like to check for missing songs? (y/n)");
                if (Console.ReadLine().ToLower() == "y")
                {
                    await CheckForMissingSongs();
                }
                await RemoveDuplicateMp4Files(downloadPath);
            }
        }

        private static async Task CheckForMissingSongs()
        {
            //remove all that say failure to download
            downloadedFingerprints = downloadedFingerprints.Where(x => x.Value != "FAILURE TO DOWNLOAD").ToDictionary(x => x.Key, x => x.Value);
            SaveDownloadedVideos();
            //list of fingerprints we found in the directories and subdirectories
            var fingerprints = new Dictionary<string, string>();
            //list of all the mp4 files in the base directory
            var mp4Files = Directory.GetFiles(GetBaseDirectory(), "*.mp4", SearchOption.AllDirectories);
            //get fingerprints of all mp4 files then search for the videoid in the downloadedFingerprints dictionary
            foreach (string mp4 in mp4Files)
            {
                var (fingerprint, duration) = await GenerateFingerprintAsync(mp4);
                if (string.IsNullOrEmpty(fingerprint) || duration < 0)
                {
                    Console.WriteLine($"Failed to generate fingerprint for {mp4}");
                    continue;
                }
                if (fingerprint != null)
                {
                    fingerprints[fingerprint] = mp4;
                }
            }
            //remove the song from the downloadedFingerprints dictionary if it is not found in the fingerprints dictionary
            foreach (var song in downloadedFingerprints.ToList())
            {
                if (!fingerprints.ContainsKey(song.Value))
                {
                    downloadedFingerprints.Remove(song.Key);
                }
            }
            SaveDownloadedVideos();
            // let the user know to restart the program to download the missing songs
            Console.WriteLine("Missing songs have been removed from the downloaded videos list. Please restart the program to download the missing songs.");
        }

        private static void InitializeDownloadedVideos()
        {
            if (File.Exists(jsonFile))
            {
                string json = File.ReadAllText(jsonFile);
                downloadedFingerprints = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
            else
            {
                downloadedFingerprints = new Dictionary<string, string>();
            }
        }

        private static string GetPlaylistUrl()
        {
            if (File.Exists("Playlist.txt"))
            {
                return File.ReadAllText("Playlist.txt");
            }
            else
            {
                Console.WriteLine("Enter the URL of the YouTube playlist you want to download: ");
                string playlistUrl = Console.ReadLine();
                File.WriteAllText("Playlist.txt", playlistUrl);
                return playlistUrl;
            }
        }

        private static string GetBaseDirectory()
        {
            if (File.Exists("dir.txt"))
            {
                return File.ReadAllText("dir.txt").Trim();
            }
            else
            {
                Console.WriteLine("Enter the directory where you want to save the playlist: ");
                string baseDirectory = Console.ReadLine().Trim();
                File.WriteAllText("dir.txt", baseDirectory);
                return baseDirectory;
            }
        }

        private static async Task<string> GetPlaylistTitle(IPage page)
        {
            var playlistTitle = await page.EvaluateExpressionAsync<string>("document.title");
            return FormatForFileSystem(playlistTitle, "_");
        }

        private static async Task ScrollToLoadAllVideos(IPage page)
        {
            int previousHeight = 0;
            int currentHeight = 0;
            while (true)
            {
                currentHeight = await page.EvaluateExpressionAsync<int>("document.documentElement.scrollHeight");
                if (currentHeight == previousHeight)
                {
                    break;
                }
                previousHeight = currentHeight;
                await page.EvaluateExpressionAsync("window.scrollTo(0, document.documentElement.scrollHeight)");
                await Task.Delay(1000);
            }
        }

        private static async Task<string[]> GetVideoLinks(IPage page)
        {
            return await page.EvaluateExpressionAsync<string[]>(
                @"Array.from(document.querySelectorAll('a#thumbnail')).map(a => a.href)");
        }

        private static bool ShouldSkipVideo(string videoId, string link)
        {
            return string.IsNullOrEmpty(videoId) || downloadedFingerprints.ContainsKey(link) || downloadedFingerprints.ContainsKey(videoId);
        }

        private static async Task<StreamManifest?> GetStreamManifestWithRetry(YoutubeClient youtube, string videoId, int maxRetries = 3, int delayMilliseconds = 1000)
        {
            int attempt = 0;
            while (attempt < maxRetries)
            {
                try
                {
                    var streamManifest = await youtube.Videos.Streams.GetManifestAsync(videoId);
                    if (streamManifest != null)
                    {
                        return streamManifest;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to get stream manifest for {videoId}: {ex.Message}");
                    attempt++;
                    if (attempt < maxRetries)
                    {
                        Console.WriteLine("Retrying in 1 second...");
                        await Task.Delay(delayMilliseconds);
                    }
                }
            }
            return null;
        }

        private static IStreamInfo GetBestStreamInfo(StreamManifest streamManifest)
        {
            var streamInfo = streamManifest.GetAudioOnlyStreams()
                .Where(s => s.Container == Container.WebM)
                .OrderByDescending(s => s.Bitrate)
                .FirstOrDefault();

            if (streamInfo == null)
            {
                streamInfo = streamManifest.GetAudioOnlyStreams()
                    .OrderByDescending(s => s.Bitrate)
                    .FirstOrDefault();
            }

            return streamInfo;
        }

        private static string RemoveTextBeforeUnderscoreHyphenUnderscore(string input)
        {
            var index = input.IndexOf("_-_");
            var indexB = input.IndexOf("__");
            if (index >= 0)
            {
                input = input.Substring(index + 2); // Remove text before and including "__"
            }
            else if (indexB >= 0)
            {
                input = input.Substring(index + 2); // Remove text before and including "__"
            }
            return input;
        }

        private static async Task<bool> DownloadWithRetry(YoutubeClient youtube, IStreamInfo streamInfo, string filePath, string videoName, int maxRetries = 3, int delayMilliseconds = 1000)
        {
            int attempt = 0;
            while (attempt < maxRetries)
            {
                try
                {
                    Console.WriteLine($"Downloading: {videoName} to {filePath} (Attempt {attempt + 1}/{maxRetries})");
                    await youtube.Videos.Streams.DownloadAsync(streamInfo, filePath);
                    Console.WriteLine($"Downloaded: {videoName} to folder {filePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to download {videoName}: {ex.Message}");
                    attempt++;
                    if (attempt < maxRetries)
                    {
                        Console.WriteLine($"Retrying in {delayMilliseconds / 1000} second(s)...");
                        await Task.Delay(delayMilliseconds);
                    }
                }
            }
            return false;
        }

        private static async Task DownloadAndQueueConversion(string link, string downloadPath)
        {
            var videoId = ExtractVideoId(link);
            if (ShouldSkipVideo(videoId, link))
            {
                Console.WriteLine($"Skipping: {videoId}");
                return;
            }

            var youtube = new YoutubeClient();
            var streamManifest = await GetStreamManifestWithRetry(youtube, videoId);
            if (streamManifest == null)
            {
                downloadedFingerprints[videoId] = "FAILURE TO DOWNLOAD";
                SaveDownloadedVideos();
                return;
            }

            var streamInfo = GetBestStreamInfo(streamManifest);
            var videoData = await youtube.Videos.GetAsync(videoId);
            var videoName = FormatForFileSystem(videoData.Title, "_");
            var filePath = Path.Combine(downloadPath, $"{videoName}.{streamInfo.Container}");

            if (!File.Exists(filePath))
            {
                bool success = await DownloadWithRetry(youtube, streamInfo, filePath, videoName);
                if (success)
                {
                    var mp4FilePath = Path.ChangeExtension(filePath, ".mp4");
                    conversionQueue.Enqueue((filePath, mp4FilePath, videoId));
                }
                else
                {
                    Console.WriteLine($"Failed to download {videoName} after 3 attempts. Skipping.");
                }
            }
        }

        private static async Task ProcessConversionQueue()
        {
            var conversionTasks = new List<Task>();

            while (!downloadingCompleted || !conversionQueue.IsEmpty)
            {
                while (conversionQueue.TryDequeue(out var item))
                {
                    await semaphore.WaitAsync();
                    conversionTasks.Add(ConvertWebMToMp4(item.webMFilePath, item.mp4FilePath, item.videoId).ContinueWith(t => semaphore.Release()));
                }

                if (conversionTasks.Count > 0)
                {
                    await Task.WhenAny(conversionTasks);
                    conversionTasks = conversionTasks.Where(t => !t.IsCompleted).ToList();
                }
                else
                {
                    await Task.Delay(100); // Small delay to avoid tight loop
                }
            }

            await Task.WhenAll(conversionTasks); // Ensure all conversions are completed
        }

        private static async Task ConvertWebMToMp4(string webMFilePath, string mp4FilePath, string videoId)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg.exe",
                    Arguments = $"-i \"{webMFilePath}\" \"{mp4FilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (sender, e) => Console.WriteLine(e.Data);
            process.ErrorDataReceived += (sender, e) => Console.WriteLine(e.Data);

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var conversionTask = process.WaitForExitAsync();
                if (await Task.WhenAny(conversionTask, Task.Delay(60000)) != conversionTask)  // 60 seconds timeout
                {
                    Console.WriteLine("Conversion timed out.");
                    process.Kill();
                }

                if (process.ExitCode == 0)
                {
                    File.Delete(webMFilePath);
                    Console.WriteLine($"Converted {webMFilePath} to {mp4FilePath}");

                    // Generate fingerprint and update dictionary
                    var (fingerprint, duration) = await GenerateFingerprintAsync(mp4FilePath);
                    if (!string.IsNullOrEmpty(fingerprint))
                    {
                        downloadedFingerprints[videoId] = fingerprint;
                        SaveDownloadedVideos();
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to convert {webMFilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred during conversion: {ex.Message}");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                process.Dispose();
            }
        }

        private static async Task RemoveDuplicateMp4Files(string directory)
        {
            Console.WriteLine("Removing duplicates may take a long time. Do you want to continue? (y/n)");
            if (Console.ReadLine().ToLower() != "y")
            {
                return;
            }

            var mp4Files = Directory.GetFiles(directory, "*.mp4", SearchOption.AllDirectories);
            var fingerprints = new Dictionary<string, string>();
            bool dontAsk = false;
            bool cleanUpName = false;

            Console.WriteLine("Do you want to delete the first duplicate found automatically without asking for confirmation? (y/n)");
            if (Console.ReadLine().ToLower() == "y")
            {
                dontAsk = true;
            }

            Console.WriteLine("Do you want to clean up the file name while comparing fingerprints? (y/n)");
            if (Console.ReadLine().ToLower() == "y")
            {
                cleanUpName = true;
            }

            foreach (string mp4 in mp4Files)
            {
                var (fingerprint, duration) = await GenerateFingerprintAsync(mp4);
                if (string.IsNullOrEmpty(fingerprint) || duration < 0)
                {
                    Console.WriteLine($"Failed to generate fingerprint for {mp4}");
                    continue;
                }

                if (cleanUpName)
                {
                    var fileName = Path.GetFileNameWithoutExtension(mp4);
                    fileName = fileName.Replace("_", " ").Replace("-", "");
                    fileName = Regex.Replace(fileName, @"\s+", " ");
                    fingerprint = $"{fileName} {fingerprint}";
                    Console.WriteLine($"Cleaned up file name: {fileName}");

                    var file = TagLib.File.Create(mp4);
                    var title = file.Tag.Title ?? string.Empty;
                    title = RemoveTextBeforeUnderscoreHyphenUnderscore(title);
                    title = title.Replace("_", " ").Replace("-", "");
                    title = Regex.Replace(title, @"\s+", " ");
                    title = Regex.Replace(title, @"\s*\(.*?\)\s*$", "");
                    file.Tag.Title = title;

                    file.Save();
                    Console.WriteLine($"Cleaned up metadata: {title}");
                }

                if (fingerprints.ContainsKey(fingerprint))
                {
                    Console.WriteLine($"Duplicate found: {mp4} and {fingerprints[fingerprint]}");

                    if (dontAsk)
                    {
                        Console.WriteLine("Deleting the first duplicate found automatically.");
                        File.Delete(mp4);
                        Console.WriteLine($"Deleted {mp4}");
                        await Task.Delay(1000);
                        continue;
                    }

                    Console.WriteLine("Do you want to delete one of them? (y/n/a)");
                    string input = Console.ReadLine().ToLower();
                    if (input == "y")
                    {
                        Console.WriteLine("Which one do you want to delete? (1/2)");
                        int choice = int.Parse(Console.ReadLine());
                        string fileToDelete = choice == 1 ? mp4 : fingerprints[fingerprint];
                        File.Delete(fileToDelete);
                        Console.WriteLine($"Deleted {fileToDelete}");
                    }
                    else if (input == "a")
                    {
                        File.Delete(mp4);
                        Console.WriteLine($"Deleted {mp4}");
                    }
                }
                else
                {
                    fingerprints[fingerprint] = mp4;
                }
            }
        }

        private static async Task<(string, int)> GenerateFingerprintAsync(string filePath, int timeoutMilliseconds = 30000)
        {
            Console.WriteLine($"Starting fingerprint generation for file: {filePath}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "fpcalc.exe",
                    Arguments = $"-length 120 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var fingerprintTask = new TaskCompletionSource<string>();
            var durationTask = new TaskCompletionSource<int>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;

                Console.WriteLine($"fpcalc output: {e.Data}");

                if (e.Data.StartsWith("FINGERPRINT="))
                {
                    var fingerprint = e.Data.Substring("FINGERPRINT=".Length);
                    fingerprintTask.TrySetResult(fingerprint);
                    Console.WriteLine("Fingerprint captured.");
                }
                else if (e.Data.StartsWith("DURATION="))
                {
                    var duration = int.Parse(e.Data.Substring("DURATION=".Length));
                    durationTask.TrySetResult(duration);
                    Console.WriteLine("Duration captured.");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"fpcalc error: {e.Data}");
                }
            };

            process.Exited += (sender, e) =>
            {
                Console.WriteLine("fpcalc process exited.");
                fingerprintTask.TrySetResult(null);
                durationTask.TrySetResult(-1);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var processExitedTask = Task.Run(() => process.WaitForExit());

            using (var cts = new CancellationTokenSource(timeoutMilliseconds))
            {
                try
                {
                    var fingerprint = await Task.WhenAny(fingerprintTask.Task, Task.Delay(Timeout.Infinite, cts.Token)) == fingerprintTask.Task
                                      ? await fingerprintTask.Task
                                      : throw new TimeoutException("Fingerprint generation timed out.");

                    var duration = await Task.WhenAny(durationTask.Task, Task.Delay(Timeout.Infinite, cts.Token)) == durationTask.Task
                                   ? await durationTask.Task
                                   : throw new TimeoutException("Fingerprint generation timed out.");

                    await Task.WhenAny(processExitedTask, Task.Delay(Timeout.Infinite, cts.Token));

                    Console.WriteLine($"Fingerprint generation completed for file: {filePath}");

                    return (fingerprint ?? string.Empty, duration);
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("Fingerprint generation process timed out.");
                    process.Kill();
                    return (string.Empty, -1);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static void CleanupFiles(string directory)
        {
            foreach (var file in Directory.GetFiles(directory, "*.webm"))
            {
                File.Delete(file);
            }

        }

        private static string FormatForFileSystem(string input, string spaceReplacement)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var cleaned = string.Join(spaceReplacement, input.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            cleaned = cleaned.Replace(" ", spaceReplacement).Trim();
            return cleaned;
        }

        private static string ExtractVideoId(string link)
        {
            var videoId = link;
            if (videoId.Contains("&list="))
            {
                videoId = videoId.Substring(0, videoId.IndexOf("&list="));
            }
            return videoId;
        }

        private static void SaveDownloadedVideos()
        {
            File.WriteAllText(jsonFile, JsonSerializer.Serialize(downloadedFingerprints));
        }
    }
}
