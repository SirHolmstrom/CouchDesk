namespace Core.Streaming;

/// <summary>
/// First-byte discriminator for binary CouchDesk WebSocket messages.
/// Values are shared with BinaryMessageType in web/app.js.
/// </summary>
public enum StreamBinaryMessageType : byte
{
    JpegTiles = 1,
    FragmentedMp4 = 3,
    H264AnnexB = 4,
    PointerMove = 5,
    CursorState = 6
}
