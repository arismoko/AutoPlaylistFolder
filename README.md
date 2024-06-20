# AutoPlaylistFolder

AutoPlaylistFolder is a C# application that downloads videos from a YouTube playlist, converts them to MP4 format, generates audio fingerprints for the downloaded songs, and saves the information into a JSON file. The application ensures that no duplicate songs are downloaded and provides functionality to remove duplicate MP4 files based on audio fingerprints.

## Features

- Download videos from a YouTube playlist.
- Convert downloaded videos to MP4 format using FFmpeg.
- Generate audio fingerprints for downloaded songs.
- Save downloaded song information into a JSON file.
- Remove duplicate MP4 files based on audio fingerprints.

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download)
- [FFmpeg](https://ffmpeg.org/download.html) - Ensure FFmpeg is installed and available in your system PATH.
- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) - A .NET library to interact with YouTube.


## How It Works

1. The application initializes by loading the existing downloaded videos information from `downloaded_videos.json`.
2. It prompts the user to enter the YouTube playlist URL and the base directory for saving the downloaded videos.
3. The application uses PuppeteerSharp to scrape the YouTube playlist and get the video links.
4. It downloads each video using YoutubeExplode and converts it to MP4 format using FFmpeg.
5. After conversion, it generates an audio fingerprint for the MP4 file using `fpcalc` (a part of Chromaprint).
6. The generated fingerprint is saved into `downloaded_videos.json` along with the video information.
7. The application can also remove duplicate MP4 files based on the generated audio fingerprints.

## Notes

- Ensure that FFmpeg is installed and available in your system PATH/project folder. You can download FFmpeg from [here](https://ffmpeg.org/download.html).
- Make sure the `fpcalc` executable (Chromaprint) is available in your system PATH/project folder for generating audio fingerprints. You can download Chromaprint from [here](https://acoustid.org/chromaprint).
- Highly recommend MusicBrainz Picard for finding metadata for downloaded songs.
