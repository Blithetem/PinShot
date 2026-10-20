namespace PinShot.App.Capture;

public enum OverlayPurpose
{
    /// <summary>截图：选区确认后进入标注编辑。</summary>
    Capture,

    /// <summary>录屏：选区确认后直接开始录制，不进入编辑。</summary>
    Record,

    /// <summary>长截图：选区确认后进入滚动拼接。</summary>
    LongShot,

    /// <summary>GIF 录制：选区确认后直接开始逐帧抓取。</summary>
    Gif,
}
