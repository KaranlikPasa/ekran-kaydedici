using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using EkranKaydedici.UI;

namespace EkranKaydedici;

internal static class Program
{
    private const string MutexName = @"Local\EkranKaydedici_SingleInstance_Mutex";
    private const string ShowEventName = @"Local\EkranKaydedici_ShowWindow_Event";

    [STAThread]
    private static void Main(string[] args)
    {
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        using var mutex = new Mutex(true, MutexName, out var createdNew);

        var background = args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));

        if (!createdNew)
        {
            if (!background)
            {
                showEvent.Set();
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(background, showEvent));
    }
}
