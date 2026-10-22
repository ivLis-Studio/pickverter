using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pickverter.Interop;

namespace Pickverter.Services;

/// Exports through Microsoft Word, Excel and PowerPoint (COM automation, never visible). A copy of Office the
/// user already has open is reused and left running with their documents untouched.
internal sealed class WinOffice : IOfficeService
{
    private readonly Dictionary<OfficeApp, bool> installed = new();

    private static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    private static string ProcessName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "WINWORD",
        OfficeApp.Excel => "EXCEL",
        _ => "POWERPNT",
    };

    public bool Has(OfficeApp app)
    {
        lock (installed)
        {
            if (installed.TryGetValue(app, out var known)) return known;
        }
        bool found;
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(ProgId(app) + @"\CLSID");
            found = key?.GetValue(null) is string;
        }
        catch (Exception)
        {
            found = false;
        }
        lock (installed) installed[app] = found;
        return found;
    }

    public async Task ExportAsync(OfficeApp app, string source, string destination, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var src = Path.GetFullPath(source);
        var dst = Path.GetFullPath(destination);
        var task = StaWorker.For("office").Run(() =>
        {
            Export(app, src, dst, target);
            return true;
        });
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(5), token));
        if (finished != task)
        {
            token.ThrowIfCancellationRequested();
            throw new PickException(L("err.export"));
        }
        await task;
        if (!File.Exists(dst) && !Directory.Exists(dst)) throw new PickException(L("err.export"));
    }

    private static void Export(OfficeApp app, string source, string destination, string target)
    {
        var wasRunning = Process.GetProcessesByName(ProcessName(app)).Length > 0;
        var type = Type.GetTypeFromProgID(ProgId(app)) ?? throw new PickException(L("err.needs_app", app.ToString()));
        dynamic? application = null;
        dynamic? document = null;
        try
        {
            application = Activator.CreateInstance(type)!;
            switch (app)
            {
                case OfficeApp.Word:
                    application.Visible = false;
                    application.DisplayAlerts = 0;
                    document = application.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true,
                        AddToRecentFiles: false, Visible: false, NoEncodingDialog: true);
                    if (target == "pdf")
                    {
                        document.ExportAsFixedFormat(OutputFileName: destination, ExportFormat: 17, OpenAfterExport: false);
                    }
                    else
                    {
                        int format = target switch
                        {
                            "docx" => 16,
                            "rtf" => 6,
                            "txt" => 2,
                            "html" => 10,
                            "odt" => 23,
                            _ => throw new PickException(L("err.cant_save_as", target.ToUpperInvariant())),
                        };
                        if (target == "txt") document.SaveAs2(FileName: destination, FileFormat: format, AddToRecentFiles: false, Encoding: 65001);
                        else document.SaveAs2(FileName: destination, FileFormat: format, AddToRecentFiles: false);
                    }
                    document.Close(SaveChanges: 0);
                    break;
                case OfficeApp.Excel:
                    application.Visible = false;
                    application.DisplayAlerts = false;
                    application.ScreenUpdating = false;
                    document = application.Workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true, AddToMru: false);
                    switch (target)
                    {
                        case "pdf":
                            document.ExportAsFixedFormat(Type: 0, Filename: destination, OpenAfterPublish: false);
                            break;
                        case "xlsx":
                            document.SaveAs(Filename: destination, FileFormat: 51);
                            break;
                        case "csv":
                            try
                            {
                                document.SaveAs(Filename: destination, FileFormat: 62);
                            }
                            catch (COMException)
                            {
                                document.SaveAs(Filename: destination, FileFormat: 6);
                            }
                            break;
                        default:
                            throw new PickException(L("err.cant_save_as", target.ToUpperInvariant()));
                    }
                    document.Close(SaveChanges: false);
                    break;
                default:
                    document = application.Presentations.Open(FileName: source, ReadOnly: -1, Untitled: 0, WithWindow: 0);
                    switch (target)
                    {
                        case "pdf":
                            document.SaveAs(destination, 32);
                            break;
                        case "pptx":
                            document.SaveAs(destination, 24);
                            break;
                        case "png":
                            Directory.CreateDirectory(destination);
                            document.Export(destination, "PNG");
                            break;
                        default:
                            throw new PickException(L("err.cant_save_as", target.ToUpperInvariant()));
                    }
                    document.Close();
                    break;
            }
        }
        catch (COMException error)
        {
            Debug.Log($"office {app}: {error.Message}");
            throw new PickException(L("err.export") + " · " + error.Message);
        }
        finally
        {
            if (document != null) Release(document);
            if (application != null)
            {
                try
                {
                    var open = app switch
                    {
                        OfficeApp.Word => (int)application.Documents.Count,
                        OfficeApp.Excel => (int)application.Workbooks.Count,
                        _ => (int)application.Presentations.Count,
                    };
                    if (!wasRunning && open == 0) application.Quit();
                }
                catch (Exception)
                {
                }
                Release(application);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void Release(object value)
    {
        try
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch (Exception)
        {
        }
    }
}
