using System;
using System.IO;
using System.Linq;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class FirstFrameMatcherTests
{
    private static BarcodeRecognitionResult Code(string text) => new(text, "CODE_128", []);

    [Fact]
    public void ExactCodeMatches()
    {
        Assert.Equal(FirstFrameCheck.Match, FirstFrameMatcher.Match([Code("SF1234567890")], "SF1234567890"));
    }

    [Fact]
    public void SeparatorsAndCaseAreIgnored()
    {
        Assert.Equal(FirstFrameCheck.Match, FirstFrameMatcher.Match([Code("sf-1234 567890")], "SF1234567890"));
    }

    [Fact]
    public void CodeContainingTrackingNumberMatches()
    {
        Assert.Equal(
            FirstFrameCheck.Match,
            FirstFrameMatcher.Match([Code("XSF1234567890Y")], "SF1234567890"));
    }

    [Fact]
    public void ShortCodeNeverMatchesByContainment()
    {
        // 面单上的 4 位分区码是目标单号的子串，但长度不足时不能算命中。
        Assert.Equal(FirstFrameCheck.Mismatch, FirstFrameMatcher.Match([Code("1234")], "SF1234567890"));
    }

    [Fact]
    public void DifferentCodeIsMismatch()
    {
        Assert.Equal(FirstFrameCheck.Mismatch, FirstFrameMatcher.Match([Code("YT0000000001")], "SF1234567890"));
    }

    [Fact]
    public void NoCodesIsUnreadable()
    {
        Assert.Equal(FirstFrameCheck.Unreadable, FirstFrameMatcher.Match([], "SF1234567890"));
    }

    [Fact]
    public void BlankTrackingNumberIsUnreadable()
    {
        Assert.Equal(FirstFrameCheck.Unreadable, FirstFrameMatcher.Match([Code("SF1234567890")], "   "));
    }
}
