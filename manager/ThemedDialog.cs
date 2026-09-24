using System.Runtime.InteropServices;

namespace GRedscriptProfiler.Manager;

internal static class ThemedDialog
{
    private static readonly Color Bg = Color.FromArgb(8, 13, 18);
    private static readonly Color Panel = Color.FromArgb(14, 23, 31);
    private static readonly Color Border = Color.FromArgb(49, 89, 104);
    private static readonly Color Text = Color.FromArgb(232, 243, 246);
    private static readonly Color Cyan = Color.FromArgb(54, 244, 244);
    private static readonly Color Magenta = Color.FromArgb(255, 63, 215);
    private static readonly Color Green = Color.FromArgb(94, 255, 130);
    private static readonly Color Amber = Color.FromArgb(255, 216, 64);
    private static readonly Color Red = Color.FromArgb(255, 82, 100);

    public static DialogResult Show(
        IWin32Window? owner,
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        using var dialog = BuildDialog(text, caption, buttons, icon);
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    private static Form BuildDialog(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        using var measureFont = new Font("Segoe UI", 9.5F);
        var measured = TextRenderer.MeasureText(
            text,
            measureFont,
            new Size(500, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        var clientWidth = Math.Clamp(measured.Width + 112, 430, 650);
        var clientHeight = Math.Clamp(measured.Height + 116, 170, 520);

        var form = new Form
        {
            Text = caption,
            ClientSize = new Size(clientWidth, clientHeight),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Bg,
            ForeColor = Text,
            Font = new Font("Segoe UI", 9.5F),
            AutoScaleMode = AutoScaleMode.Dpi
        };

        try
        {
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Branding is cosmetic; never block a message dialog.
        }

        var accent = IconAccent(icon);
        var topAccent = new Panel
        {
            Dock = DockStyle.Top,
            Height = 2,
            BackColor = accent
        };

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(18, 18, 18, 12)
        };

        var systemIcon = new PictureBox
        {
            Left = 18,
            Top = 20,
            Width = 42,
            Height = 42,
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.Transparent,
            Image = GetSystemIcon(icon)?.ToBitmap()
        };

        var message = new RichTextBox
        {
            Left = 76,
            Top = 18,
            Width = clientWidth - 94,
            Height = clientHeight - 88,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
            TabStop = false,
            Text = text,
            ForeColor = Text,
            BackColor = Bg
        };

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = Panel,
            Padding = new Padding(12)
        };

        body.Controls.Add(systemIcon);
        body.Controls.Add(message);
        form.Controls.Add(body);
        form.Controls.Add(buttonPanel);
        form.Controls.Add(topAccent);

        ConfigureButtons(form, buttonPanel, buttons, accent);

        form.Shown += (_, _) => ApplyDarkTitleBar(form);
        return form;
    }

    private static void ConfigureButtons(Form form, Panel panel, MessageBoxButtons buttons, Color accent)
    {
        var specs = buttons switch
        {
            MessageBoxButtons.YesNo => new[]
            {
                ("Yes", DialogResult.Yes, true),
                ("No", DialogResult.No, false)
            },
            MessageBoxButtons.OKCancel => new[]
            {
                ("OK", DialogResult.OK, true),
                ("Cancel", DialogResult.Cancel, false)
            },
            _ => new[]
            {
                ("OK", DialogResult.OK, true)
            }
        };

        var right = form.ClientSize.Width - 12;
        Button? defaultButton = null;
        Button? cancelButton = null;

        for (var i = specs.Length - 1; i >= 0; i--)
        {
            var spec = specs[i];
            var button = new Button
            {
                Text = spec.Item1,
                DialogResult = spec.Item2,
                Width = 84,
                Height = 32,
                Top = 15,
                Left = right - 84,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Bg,
                ForeColor = spec.Item3 ? accent : Text,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = spec.Item3 ? accent : Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(19, 35, 44);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(23, 43, 53);

            panel.Controls.Add(button);
            right -= 96;

            if (spec.Item3)
                defaultButton = button;
            if (spec.Item2 is DialogResult.No or DialogResult.Cancel)
                cancelButton = button;
        }

        if (defaultButton is not null)
            form.AcceptButton = defaultButton;
        if (cancelButton is not null)
            form.CancelButton = cancelButton;
    }

    private static Icon? GetSystemIcon(MessageBoxIcon icon) =>
        icon switch
        {
            MessageBoxIcon.Error => SystemIcons.Error,
            MessageBoxIcon.Warning => SystemIcons.Warning,
            MessageBoxIcon.Information => SystemIcons.Information,
            MessageBoxIcon.Question => SystemIcons.Question,
            _ => SystemIcons.Information
        };

    private static Color IconAccent(MessageBoxIcon icon) =>
        icon switch
        {
            MessageBoxIcon.Error => Red,
            MessageBoxIcon.Warning => Amber,
            MessageBoxIcon.Information => Cyan,
            MessageBoxIcon.Question => Magenta,
            _ => Green
        };

    public static void ApplyDarkTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows() || form.IsDisposed)
            return;

        var handle = form.Handle;
        if (handle == IntPtr.Zero)
            return;

        var enabled = 1;
        try
        {
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        catch
        {
            // Older Windows versions may not support the attribute.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
