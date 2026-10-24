using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AlexisScreenPro.Helpers;

/// <summary>
/// Applies the Windows composition layer that makes the cobalt glass feel
/// native: dark DWM chrome, rounded corners, a subtle acrylic blur, and
/// exclusion from the app's own full-screen captures.
/// </summary>
public static class WindowEffects
{
    private const int CobaltAcrylicTint = unchecked((int)0xA62E1508); // AABBGGRR

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsInitialized)
        {
            window.SourceInitialized += (_, _) => Apply(window);
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ApplyDarkChrome(handle);
        ApplyRoundedCorners(handle);
        ApplyAcrylic(handle);
        MarkAsToolWindow(handle);
        ExcludeFromCapture(handle);
    }

    public static void PlaceBottomRight(Window window, double edgeInset = 18)
    {
        var workArea = SystemParameters.WorkArea;
        var width = window.Width;
        var height = window.Height;
        window.Left = Math.Max(workArea.Left + 8, workArea.Right - width - edgeInset);
        window.Top = Math.Max(workArea.Top + 8, workArea.Bottom - height - edgeInset);
    }

    public static void PlaceCentered(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Left + Math.Max(0, (workArea.Width - window.Width) / 2);
        window.Top = workArea.Top + Math.Max(0, (workArea.Height - window.Height) / 2);
    }

    private static void ApplyDarkChrome(IntPtr handle)
    {
        try
        {
            var enabled = 1;
            var result = NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));

            if (result != 0)
            {
                _ = NativeMethods.DwmSetWindowAttribute(
                    handle,
                    NativeMethods.DwmUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows builds simply retain the WPF chrome.
        }
    }

    private static void ApplyRoundedCorners(IntPtr handle)
    {
        try
        {
            var preference = NativeMethods.DwmRoundCorner;
            _ = NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DwmWindowCornerPreference,
                ref preference,
                sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // WPF clipping still provides rounded corners.
        }
    }

    private static void ApplyAcrylic(IntPtr handle)
    {
        var policy = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = CobaltAcrylicTint,
            AnimationId = 0
        };

        var policySize = Marshal.SizeOf<NativeMethods.AccentPolicy>();
        var policyPointer = Marshal.AllocHGlobal(policySize);

        try
        {
            Marshal.StructureToPtr(policy, policyPointer, fDeleteOld: false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WcaAccentPolicy,
                Data = policyPointer,
                SizeOfData = policySize
            };

            _ = NativeMethods.SetWindowCompositionAttribute(handle, ref data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The translucent WPF glass remains fully usable.
        }
        finally
        {
            Marshal.FreeHGlobal(policyPointer);
        }
    }

    private static void MarkAsToolWindow(IntPtr handle)
    {
        try
        {
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            style |= NativeMethods.WsExToolWindow;
            style &= ~NativeMethods.WsExAppWindow;
            _ = NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Window still stays out of the taskbar through ShowInTaskbar=false.
        }
    }

    private static void ExcludeFromCapture(IntPtr handle)
    {
        if (Environment.GetEnvironmentVariable("ASP_DISABLE_CAPTURE_EXCLUSION") == "1")
        {
            return;
        }

        try
        {
            _ = NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows 10 2004+ supports this; older builds may not.
        }
    }
}
