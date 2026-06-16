using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using YouTubeDownloader.Models;

namespace YouTubeDownloader.Services;

internal static class YtDlpMetadataParser
{
    public static YtDlpAnalyzeResult ParsePlaylistFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var playlist = new PlaylistMetadata
            {
                Id = GetStringProperty(root, "id") ?? "",
                Title = GetStringProperty(root, "title") ?? "プレイリスト",
                Channel = GetStringProperty(root, "uploader") ?? GetStringProperty(root, "channel") ?? "",
                ThumbnailUrl = GetThumbnailUrl(root)
            };

            var videos = new List<VideoMetadata>();

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                int index = 1;
                foreach (var entry in entries.EnumerateArray())
                {
                    var videoId = GetStringProperty(entry, "id") ?? "";
                    var video = new VideoMetadata
                    {
                        Id = videoId,
                        Title = GetStringProperty(entry, "title") ?? $"動画 {index}",
                        Channel = GetStringProperty(entry, "uploader") ?? GetStringProperty(entry, "channel") ?? playlist.Channel,
                        DurationSeconds = GetIntProperty(entry, "duration"),
                        ThumbnailUrl = GetThumbnailUrl(entry),
                        // 常にYouTube動画URLを正しく構築
                        Url = !string.IsNullOrEmpty(videoId) ? $"https://www.youtube.com/watch?v={videoId}" : "",
                        PlaylistId = playlist.Id,
                        PlaylistIndex = index
                    };
                    videos.Add(video);
                    index++;
                }
            }

            playlist.Videos = videos;
            playlist.VideoCount = videos.Count;

            // YouTube上の総数(playlist_count)。取得できなければ実取得数にそろえる。
            // 実取得数より大きい場合は「取得漏れ」(古いyt-dlpのページネーション不具合など)を意味する。
            var totalCount = GetIntProperty(root, "playlist_count");
            playlist.TotalVideoCount = totalCount > videos.Count ? totalCount : videos.Count;

            return new YtDlpAnalyzeResult
            {
                IsSuccess = true,
                IsPlaylist = true,
                PlaylistMetadata = playlist
            };
        }
        catch (Exception ex)
        {
            return new YtDlpAnalyzeResult
            {
                IsSuccess = false,
                ErrorMessage = $"プレイリスト解析エラー: {ex.Message}"
            };
        }
    }

    public static VideoMetadata? ParseVideoMetadata(string json, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var publishDateStr = GetStringProperty(root, "upload_date");
            DateTime? publishDate = null;
            if (!string.IsNullOrEmpty(publishDateStr) && publishDateStr.Length == 8)
            {
                if (DateTime.TryParseExact(publishDateStr, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    publishDate = parsed;
                }
            }

            return new VideoMetadata
            {
                Id = GetStringProperty(root, "id") ?? "",
                Title = GetStringProperty(root, "title") ?? "",
                Channel = GetStringProperty(root, "uploader") ?? GetStringProperty(root, "channel") ?? "",
                DurationSeconds = GetIntProperty(root, "duration"),
                PublishDate = publishDate,
                ThumbnailUrl = GetThumbnailUrl(root),
                Url = GetStringProperty(root, "webpage_url") ?? url
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static int GetIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                if (prop.TryGetInt32(out var intResult))
                {
                    return intResult;
                }

                if (prop.TryGetDouble(out var doubleResult))
                {
                    return ToInt32OrDefault(doubleResult);
                }
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return ToInt32OrDefault(doubleResult);
                }
            }
        }
        return 0;
    }

    private static int ToInt32OrDefault(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        if (value > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (value < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static string GetThumbnailUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        // thumbnailプロパティを優先
        if (element.TryGetProperty("thumbnail", out var thumb) && thumb.ValueKind == JsonValueKind.String)
        {
            return thumb.GetString() ?? "";
        }

        // thumbnails配列から取得
        if (element.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            JsonElement? bestThumb = null;
            int maxWidth = 0;

            foreach (var t in thumbs.EnumerateArray())
            {
                var width = GetIntProperty(t, "width");
                if (width > maxWidth || bestThumb == null)
                {
                    maxWidth = width;
                    bestThumb = t;
                }
            }

            if (bestThumb.HasValue)
            {
                return GetStringProperty(bestThumb.Value, "url") ?? "";
            }
        }

        return "";
    }
}
