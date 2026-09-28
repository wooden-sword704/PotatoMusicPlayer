using System.Collections.Generic;

namespace PotatoMusicPlayer.Models
{
    /// <summary>
    /// プレイリストの1項目。FilePath は通常絶対パス。
    /// 音声パック時は zip 内の相対パス (audio/...) になる。
    /// </summary>
    public class PlaylistEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public System.DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// 保存されたプレイリスト。設定ファイルに永続化される。
    /// </summary>
    public class StoredPlaylist
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public List<PlaylistEntry> Entries { get; set; } = new List<PlaylistEntry>();
    }

    /// <summary>
    /// zip 内の playlist.json に対応する形式。
    /// </summary>
    public class PlaylistFile
    {
        public string Name { get; set; } = string.Empty;
        public List<PlaylistEntry> Entries { get; set; } = new List<PlaylistEntry>();
    }
}
