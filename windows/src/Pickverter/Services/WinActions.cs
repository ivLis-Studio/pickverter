using System.Windows;
using Microsoft.Win32;
using Pickverter.Interop;
using Pickverter.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Pickverter.Services;

/// Share (Nearby sharing, Mail, Teams…), Open with, Float and the folder picker.
internal sealed class WinActions : IActionsService
{
    public Task ShareAsync(IReadOnlyList<string> files, ScreenPoint anchor) => Platform.Main.RunAsync(() => ShareSheet.Show(files, anchor));

    public void OpenWith(string file)
    {
        _ = StaWorker.Run("open with", () =>
        {
            var info = new Native.OPENASINFO
            {
                pcszFile = Path.GetFullPath(file),
                oaifInFlags = Native.OAIF_ALLOW_REGISTRATION | Native.OAIF_EXEC,
            };
            return Native.SHOpenWithDialog(IntPtr.Zero, ref info);
        }, TimeSpan.FromMinutes(10), 0);
    }

    public void Float(IReadOnlyList<string> files, ScreenPoint anchor) => Platform.Main.Post(() => FloatWindow.Show(files, anchor));

    public Task<string?> PickFolderAsync(bool move, int count, string? initial) => Platform.Main.RunAsync<string?>(() =>
    {
        var dialog = new OpenFolderDialog
        {
            Title = LP(move ? "panel.move.message" : "panel.copy.message", count),
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        var owner = Windowing.Owner();
        try
        {
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
        }
        finally
        {
            owner.Close();
        }
    });
}

/// The Windows share sheet needs a window to belong to: a tiny invisible one at the wheel's position.
internal static class ShareSheet
{
    public static void Show(IReadOnlyList<string> files, ScreenPoint anchor)
    {
        var host = Windowing.Owner(anchor);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(host).Handle;
        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = ShellIds.IID_DataTransferManager;
            var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));
            manager.DataRequested += async (_, args) =>
            {
                var deferral = args.Request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var path in files)
                    {
                        var full = Path.GetFullPath(path);
                        if (Directory.Exists(full)) items.Add(await StorageFolder.GetFolderFromPathAsync(full));
                        else items.Add(await StorageFile.GetFileFromPathAsync(full));
                    }
                    var data = args.Request.Data;
                    data.Properties.Title = files.Count == 1 ? FileNames.Name(files[0]) : LP("wheel.items", files.Count);
                    data.Properties.ApplicationName = "Pickverter";
                    data.SetStorageItems(items, true);
                    data.ShareCompleted += (_, _) => Platform.Main.Post(host.Close);
                    data.ShareCanceled += (_, _) => Platform.Main.Post(host.Close);
                }
                catch (Exception error)
                {
                    Debug.Log("share: " + error.Message);
                    args.Request.FailWithDisplayText(L("err.no_result"));
                }
                finally
                {
                    deferral.Complete();
                }
            };
            interop.ShowShareUIForWindow(hwnd);
            // Whatever happens, never leave the helper window behind.
            Platform.Main.After(TimeSpan.FromMinutes(3), () => host.Close());
        }
        catch (Exception error)
        {
            Debug.Log("share: " + error.Message);
            host.Close();
            throw new PickException(L("err.airdrop"));
        }
    }
}
