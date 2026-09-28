using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using PotatoMusicPlayer.Models;

namespace PotatoMusicPlayer.Services
{
    /// <summary>
    /// プレイリスト(zip)のエクスポート/インポート。
    /// 形式: playlist.json + 任意の audio/ 配下の音声ファイル。
    /// パック時は FilePath を zip 内相対パス (audio/...) で保存する。
    /// </summary>
    public static class PlaylistService
    {
        public const string PlaylistJsonName = "playlist.json";
        public const string AudioFolderName = "audio";

        /// <summary>音声パック時にこの合計サイズを超えると警告する。</summary>
        public const long PackSizeWarningThresholdBytes = 500L * 1024 * 1024;

        /// <summary>パックした音声の展開先。なければ作成する。</summary>
        public static string MusicDirectory => Path.Combine(AppContext.BaseDirectory, "music");

        public sealed class ExportResult
        {
            public int PackedCount { get; set; }
            public int SkippedCount { get; set; }
            public long TotalBytes { get; set; }
        }

        public sealed class ImportResult
        {
            public string Name { get; set; } = string.Empty;
            public List<PlaylistEntry> Entries { get; set; } = new List<PlaylistEntry>();
            public int ExtractedCount { get; set; }
        }

        public static long EstimatePackSize(IEnumerable<PlaylistEntry> entries)
        {
            long total = 0;
            foreach (var entry in entries ?? Enumerable.Empty<PlaylistEntry>())
            {
                try
                {
                    if (!string.IsNullOrEmpty(entry?.FilePath) && File.Exists(entry.FilePath))
                        total += new FileInfo(entry.FilePath).Length;
                }
                catch
                {
                    // サイズ取得に失敗した項目は 0 扱い
                }
            }
            return total;
        }

        public static ExportResult Export(string zipPath, string name, IList<PlaylistEntry> entries, bool packAudio)
        {
            var result = new ExportResult();
            var stored = new List<PlaylistEntry>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var entry in entries ?? Enumerable.Empty<PlaylistEntry>())
                {
                    if (entry == null)
                        continue;

                    if (packAudio && !string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath))
                    {
                        string uniqueName = Uniquify(Path.GetFileName(entry.FilePath), usedNames);
                        usedNames.Add(uniqueName);
                        string zipEntryName = AudioFolderName + "/" + uniqueName;
                        archive.CreateEntryFromFile(entry.FilePath, zipEntryName, CompressionLevel.Optimal);
                        result.TotalBytes += new FileInfo(entry.FilePath).Length;
                        result.PackedCount++;
                        stored.Add(new PlaylistEntry { FileName = entry.FileName, FilePath = zipEntryName, AddedAt = entry.AddedAt });
                    }
                    else if (!packAudio)
                    {
                        stored.Add(new PlaylistEntry { FileName = entry.FileName, FilePath = entry.FilePath, AddedAt = entry.AddedAt });
                    }
                    else
                    {
                        result.SkippedCount++;
                    }
                }

                var playlistFile = new PlaylistFile { Name = name ?? string.Empty, Entries = stored };
                var jsonEntry = archive.CreateEntry(PlaylistJsonName);
                using (var writer = new StreamWriter(jsonEntry.Open()))
                    writer.Write(JsonConvert.SerializeObject(playlistFile, Formatting.Indented));
            }

            return result;
        }

        public static ImportResult Import(string zipPath)
        {
            var result = new ImportResult();

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var jsonEntry = archive.GetEntry(PlaylistJsonName);
                if (jsonEntry == null)
                    throw new InvalidDataException("Playlist json not found in archive.");

                PlaylistFile playlistFile;
                using (var reader = new StreamReader(jsonEntry.Open()))
                    playlistFile = JsonConvert.DeserializeObject<PlaylistFile>(reader.ReadToEnd());

                if (playlistFile == null)
                    throw new InvalidDataException("Playlist json could not be read.");

                result.Name = playlistFile.Name ?? string.Empty;
                Directory.CreateDirectory(MusicDirectory);

                foreach (var entry in playlistFile.Entries ?? Enumerable.Empty<PlaylistEntry>())
                {
                    if (entry == null)
                        continue;

                    string filePath = entry.FilePath ?? string.Empty;
                    bool isPacked = filePath.StartsWith(AudioFolderName + "/", StringComparison.OrdinalIgnoreCase) ||
                                    filePath.StartsWith(AudioFolderName + "\\", StringComparison.OrdinalIgnoreCase);
                    var audioEntry = isPacked ? archive.GetEntry(filePath.Replace('\\', '/')) : null;

                    if (isPacked && audioEntry != null)
                    {
                        string targetName = UniquifyFileName(MusicDirectory, Path.GetFileName(filePath));
                        string targetPath = Path.Combine(MusicDirectory, targetName);
                        audioEntry.ExtractToFile(targetPath);
                        result.ExtractedCount++;
                        result.Entries.Add(new PlaylistEntry { FileName = entry.FileName, FilePath = targetPath, AddedAt = OrNow(entry.AddedAt) });
                    }
                    else
                    {
                        result.Entries.Add(new PlaylistEntry { FileName = entry.FileName, FilePath = filePath, AddedAt = OrNow(entry.AddedAt) });
                    }
                }
            }

            return result;
        }

        private static DateTime OrNow(DateTime value) =>
            value == DateTime.MinValue ? DateTime.Now : value;

        public static string FormatBytes(long bytes)        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / (1024.0 * 1024 * 1024)).ToString("0.#") + " GB";
            if (bytes >= 1024L * 1024)
                return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / 1024.0).ToString("0.#") + " KB";
        }

        private static string Uniquify(string fileName, HashSet<string> usedNames)
        {
            if (string.IsNullOrEmpty(fileName))
                fileName = "audio";
            if (!usedNames.Contains(fileName))
                return fileName;

            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int number = 1;
            string candidate;
            do
            {
                number++;
                candidate = stem + " (" + number + ")" + ext;
            } while (usedNames.Contains(candidate));
            return candidate;
        }

        private static string UniquifyFileName(string directory, string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                fileName = "audio";
            string candidate = fileName;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int number = 1;
            while (File.Exists(Path.Combine(directory, candidate)))
            {
                number++;
                candidate = stem + " (" + number + ")" + ext;
            }
            return candidate;
        }
    }
}
