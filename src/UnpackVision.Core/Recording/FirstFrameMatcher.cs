using System;

namespace UnpackVision.Core;

/// <summary>
/// 首帧面单回读判定。面单上常同时印有快递条码、商品条码和仓库内部流转码，
/// 因此按“任一码命中单号即通过”判定；只有全部码都不匹配时才算不符，
/// 避免把贴在同一张面单上的辅助条码误判成扫错单。
/// </summary>
public static class FirstFrameMatcher
{
    /// <summary>短码（少于该长度）不参与“包含”判定，否则 4 位分区码会命中任意单号。</summary>
    private const int MinimumContainedLength = 8;

    public static FirstFrameCheck Match(IReadOnlyList<BarcodeRecognitionResult> codes, string trackingNo)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count == 0)
        {
            return FirstFrameCheck.Unreadable;
        }

        var target = Normalize(trackingNo);
        if (target.Length == 0)
        {
            return FirstFrameCheck.Unreadable;
        }

        foreach (var code in codes)
        {
            var text = Normalize(code.Text);
            if (text.Length == 0)
            {
                continue;
            }
            if (string.Equals(text, target, StringComparison.Ordinal) ||
                text.Contains(target, StringComparison.Ordinal) ||
                (text.Length >= MinimumContainedLength && target.Contains(text, StringComparison.Ordinal)))
            {
                return FirstFrameCheck.Match;
            }
        }

        return FirstFrameCheck.Mismatch;
    }

    /// <summary>
    /// 条码读出的文本常带空格、换行，部分面单把单号分段印刷并插入连字符；
    /// 单号本身区分大小写场景很少，统一转大写后比对。
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '-' or '_')
            {
                continue;
            }
            buffer[length++] = char.ToUpperInvariant(character);
        }
        return new string(buffer, 0, length);
    }
}
