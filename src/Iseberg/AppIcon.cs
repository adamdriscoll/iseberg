using Avalonia.Controls;

namespace Iseberg;

public static class AppIcon
{
    public static WindowIcon Create()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write((byte)16); writer.Write((byte)16); writer.Write((ushort)0);
        writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(40 + 16 * 16 * 4 + 16 * 4); writer.Write(22);
        writer.Write(40); writer.Write(16); writer.Write(32); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(16 * 16 * 4); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (var y = 15; y >= 0; y--)
            for (var x = 0; x < 16; x++)
            {
                var foreground = (y is >= 4 and <= 7 && x == y - 1) || (y is >= 8 and <= 11 && x == 14 - y) ||
                    (y == 11 && x is >= 8 and <= 12);
                writer.Write(foreground ? (byte)255 : (byte)170);
                writer.Write(foreground ? (byte)255 : (byte)125);
                writer.Write(foreground ? (byte)255 : (byte)25);
                writer.Write((byte)255);
            }
        writer.Write(new byte[64]);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
