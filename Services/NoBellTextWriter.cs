using System;
using System.IO;
using System.Text;

namespace mnmbox_tui.Services;

/// <summary>
/// TextWriter wrapper that filters out BEL (0x07) characters to prevent console beeping
/// </summary>
public class NoBellTextWriter : TextWriter
{
    private readonly TextWriter _inner;

    public NoBellTextWriter(TextWriter inner)
    {
        _inner = inner;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        if (value != '\x07') // Filter BEL character
            _inner.Write(value);
    }

    public override void Write(string? value)
    {
        if (value != null)
            _inner.Write(value.Replace("\x07", ""));
    }

    public override void Write(char[] buffer, int index, int count)
    {
        var filtered = new char[count];
        int j = 0;
        for (int i = 0; i < count; i++)
        {
            if (buffer[index + i] != '\x07')
                filtered[j++] = buffer[index + i];
        }
        _inner.Write(filtered, 0, j);
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override void Close()
    {
        _inner.Close();
    }
}
