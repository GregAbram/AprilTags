using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// A room config as compact text, for a QR code posted in the room:
//   TACCROOM1|<name>|<dataManager>|<default tag size>|<id>,<x>,<y>,<z>,<yaw>[,<size>];...
// Positions in meters to the millimeter, yaw to 0.1 deg, sizes to 0.1 mm; a
// tag's size is given only when it differs from the default. Every tag in a
// code is measured (it comes from a survey), and unlisted tags are learned at
// the default size. About 30 bytes per tag. The name and dataManager may not
// contain '|' (replaced by '/').
//
// SurveyId is a short hash of the tags' positions, shown to viewers so they can
// see at a glance that they use the same survey.
public static class RoomCode
{
    public const string Prefix = "TACCROOM1";

    public static string Encode(RoomConfig config)
    {
        var size = DefaultSize(config);
        var text = new StringBuilder();
        text.Append(Prefix).Append('|')
            .Append(Clean(config.name)).Append('|')
            .Append(Clean(config.dataManager)).Append('|')
            .Append(F(size, "0.0000")).Append('|')
            .Append(TagList(config, size));
        return text.ToString();
    }

    // Null if the text isn't a room code (e.g. some other QR code).
    public static RoomConfig Decode(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix + "|", StringComparison.Ordinal))
        {
            return null;
        }
        var parts = text.Split('|');
        if (parts.Length != 5 || !TryF(parts[3], out var size) || size <= 0f)
        {
            return null;
        }
        var tags = new List<TagPlacement>();
        foreach (var entry in parts[4].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = entry.Split(',');
            if (f.Length < 5 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                !TryF(f[1], out var x) || !TryF(f[2], out var y) || !TryF(f[3], out var z) || !TryF(f[4], out var yaw))
            {
                return null;
            }
            var tagSize = size;
            if (f.Length > 5 && !TryF(f[5], out tagSize))
            {
                return null;
            }
            tags.Add(new TagPlacement { id = id, x = x, y = y, z = z, yawDegrees = yaw, sizeMeters = tagSize, measured = true });
        }
        return new RoomConfig
        {
            name = parts[1],
            dataManager = parts[2],
            learnUnlistedTags = true,
            defaultTagSizeMeters = size,
            tags = tags.ToArray(),
        };
    }

    // Six characters identifying the measured tags' positions and sizes.
    public static string SurveyId(RoomConfig config)
    {
        if (config?.tags == null || config.tags.Length == 0)
        {
            return "------";
        }
        // FNV-1a over the canonical tag list, in Crockford base32.
        var hash = 2166136261u;
        foreach (var c in TagList(config, DefaultSize(config)))
        {
            hash = (hash ^ c) * 16777619u;
        }
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var id = new char[6];
        for (var i = 0; i < 6; i++)
        {
            id[i] = alphabet[(int)(hash & 31)];
            hash >>= 5;
        }
        return new string(id);
    }

    private static string TagList(RoomConfig config, float defaultSize)
    {
        var text = new StringBuilder();
        var anyMeasured = config.tags != null && Array.Exists(config.tags, t => t.measured);
        foreach (var tag in config.tags ?? Array.Empty<TagPlacement>())
        {
            if (anyMeasured && !tag.measured)
            {
                continue;
            }
            if (text.Length > 0)
            {
                text.Append(';');
            }
            text.Append(tag.id.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(F(tag.x, "0.000")).Append(',')
                .Append(F(tag.y, "0.000")).Append(',')
                .Append(F(tag.z, "0.000")).Append(',')
                .Append(F(tag.yawDegrees, "0.0"));
            if (Math.Abs(tag.sizeMeters - defaultSize) > 0.00005f)
            {
                text.Append(',').Append(F(tag.sizeMeters, "0.0000"));
            }
        }
        return text.ToString();
    }

    private static float DefaultSize(RoomConfig config)
    {
        if (config.defaultTagSizeMeters > 0f)
        {
            return config.defaultTagSizeMeters;
        }
        return config.tags != null && config.tags.Length > 0 ? config.tags[0].sizeMeters : 0f;
    }

    private static string Clean(string text) => (text ?? "").Replace('|', '/').Trim();

    private static string F(float value, string format)
    {
        var s = value.ToString(format, CultureInfo.InvariantCulture);
        return s == "-0.000" || s == "-0.0" || s == "-0.0000" ? s.Substring(1) : s;
    }

    private static bool TryF(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
