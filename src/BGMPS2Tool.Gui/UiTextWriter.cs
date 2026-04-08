using System.Text;
using System.Windows.Forms;

namespace BGMPS2Tool.Gui;

internal sealed class UiTextWriter : TextWriter
{
    private readonly TextBox _target;

    public UiTextWriter(TextBox target)
    {
        _target = target;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        Append(value + Environment.NewLine);
    }

    public override void Write(char value)
    {
        Append(value.ToString());
    }

    private void Append(string text)
    {
        if (_target.IsDisposed)
        {
            return;
        }

        if (_target.InvokeRequired)
        {
            _target.BeginInvoke(() => Append(text));
            return;
        }

        _target.AppendText(text);
    }
}
