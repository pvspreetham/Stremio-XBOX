using System.Collections.Generic;
using Windows.Data.Json;

namespace StremioXbox.Core
{
    /// <summary>A stream handed over from Stremio Web for native playback.</summary>
    public sealed class PlayRequest
    {
        /// <summary>Gets the HTTP(S) URL of the file.</summary>
        public string Url { get; private set; }

        /// <summary>Gets the addon's stream name (often holds quality tags).</summary>
        public string Name { get; private set; }

        /// <summary>Gets the addon's stream description (often holds the file name).</summary>
        public string Description { get; private set; }

        /// <summary>Gets the file name from behaviorHints, if the addon set one.</summary>
        public string FileName { get; private set; }

        /// <summary>Gets a value indicating whether the addon asked for custom request headers.</summary>
        public bool HasRequestHeaders { get; private set; }

        /// <summary>Gets external subtitles collected from the stream and the user's subtitle addons.</summary>
        public IReadOnlyList<SubtitleSource> Subtitles { get; private set; } = new List<SubtitleSource>();

        /// <summary>Builds a request from the shell script's "play" message.</summary>
        public static PlayRequest FromJson(JsonObject message)
        {
            var subtitles = new List<SubtitleSource>();
            if (message.ContainsKey("subtitles") && message["subtitles"].ValueType == JsonValueType.Array)
            {
                foreach (var value in message.GetNamedArray("subtitles"))
                {
                    if (value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var sub = value.GetObject();
                    var url = sub.GetNamedString("url", string.Empty);
                    if (url.Length > 0)
                    {
                        subtitles.Add(new SubtitleSource(url, sub.GetNamedString("lang", string.Empty), sub.GetNamedString("label", string.Empty)));
                    }
                }
            }

            return new PlayRequest
            {
                Subtitles = subtitles,
                Url = message.GetNamedString("url", string.Empty),
                Name = message.GetNamedString("name", string.Empty),
                Description = message.GetNamedString("description", string.Empty),
                FileName = message.GetNamedString("filename", string.Empty),
                HasRequestHeaders = message.GetNamedBoolean("hasRequestHeaders", false),
            };
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var label = string.IsNullOrEmpty(FileName) ? Description : FileName;
            return (Name + " | " + label).Replace("\n", " ");
        }
    }

    /// <summary>An external subtitle file (SRT/VTT) offered by the stream or an addon.</summary>
    public sealed class SubtitleSource
    {
        public SubtitleSource(string url, string language, string label)
        {
            Url = url;
            Language = language;
            Label = label;
        }

        public string Url { get; }

        public string Language { get; }

        public string Label { get; }
    }
}
