Add-Type -ReferencedAssemblies 'System.dll','System.Windows.Forms.dll','System.Drawing.dll' -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class DevelopmentConsentForm : Form {
    private const int SessionChange = 0x02B1;
    private const int ConsoleDisconnect = 0x2;
    private const int SessionLogoff = 0x6;
    private const int SessionLock = 0x7;
    private const int ThisSession = 0;
    private IntPtr registeredHandle;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);

    protected override void OnHandleCreated(EventArgs args) {
        base.OnHandleCreated(args);
        if (!WTSRegisterSessionNotification(Handle, ThisSession))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        registeredHandle = Handle;
    }
    protected override void OnHandleDestroyed(EventArgs args) {
        if (registeredHandle != IntPtr.Zero) {
            WTSUnRegisterSessionNotification(registeredHandle);
            registeredHandle = IntPtr.Zero;
        }
        base.OnHandleDestroyed(args);
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == SessionChange) {
            int reason = message.WParam.ToInt32();
            if (reason == ConsoleDisconnect || reason == SessionLogoff || reason == SessionLock) {
                Close();
                return;
            }
        }
        base.WndProc(ref message);
    }
}
'@
$form = New-Object DevelopmentConsentForm
$form.FormBorderStyle = 'FixedDialog'
$form.MinimizeBox = $false
$form.MaximizeBox = $false
