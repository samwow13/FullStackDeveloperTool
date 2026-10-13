using System.IO;

namespace FullStackLauncher.Services;

/// <summary>Creates the completion notification chime for the independent Codex monitor.</summary>
internal static class ReminderSound
{
    public static byte[] CreateChime()
    {
        const int rate = 22050;
        const int samples = rate / 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / rate;
            var segment = t < .25 ? t : t - .25;
            var envelope = Math.Min(segment / .015, 1) * Math.Max(0, 1 - segment / .25);
            writer.Write((short)(Math.Sin(2 * Math.PI * (t < .25 ? 660 : 880) * segment) * envelope * 7000));
        }
        return stream.ToArray();
    }
}
