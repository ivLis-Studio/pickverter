using System.Runtime.InteropServices;

namespace Pickverter.Interop;

// Shell COM interfaces, declared only up to the last method Pickverter calls (vtable order matters).

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object Item(object index);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object _NewEnum();

    void Register([MarshalAs(UnmanagedType.IDispatch)] object dispatch, int hwnd, int swClass, out int cookie);
    void RegisterPending(int threadId, ref object location, ref object locationRoot, int swClass, out int cookie);
    void Revoke(int cookie);
    void OnNavigate(int cookie, ref object location);
    void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW(ref object location, ref object locationRoot, int swClass, out int hwnd, int options);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? result);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);
    [PreserveSig] int ContextSensitiveHelp(int enter);
    [PreserveSig] int InsertMenusSB(IntPtr menu, IntPtr widths);
    [PreserveSig] int SetMenuSB(IntPtr menu, IntPtr olemenu, IntPtr activeObject);
    [PreserveSig] int RemoveMenusSB(IntPtr menu);
    [PreserveSig] int SetStatusTextSB(IntPtr text);
    [PreserveSig] int EnableModelessSB(int enable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
    [PreserveSig] int BrowseObject(IntPtr pidl, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
    [PreserveSig] int GetControlWindow(uint id, out IntPtr hwnd);
    [PreserveSig] int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView? view);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);
}

[ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint mode);
    [PreserveSig] int SetCurrentViewMode(uint mode);
    [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? folder);
    [PreserveSig] int Item(int index, out IntPtr pidl);
    [PreserveSig] int ItemCount(uint flags, out int count);
    [PreserveSig] int Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? items);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetAttributes(int flags, uint mask, out uint attributes);
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemAt(uint index, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
    [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem? parent);
    [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Width;
    public int Height;

    public NativeSize(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
}

[ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataTransferManagerInterop
{
    IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
    void ShowShareUIForWindow(IntPtr appWindow);
}

internal static class ShellIds
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static Guid IID_IShellBrowser = typeof(IShellBrowser).GUID;
    public static Guid IID_IShellItemArray = typeof(IShellItemArray).GUID;
    public static readonly Guid IID_DataTransferManager = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public const uint SVGIO_SELECTION = 0x1, SVGIO_FLAG_VIEWORDER = 0x80000000;
    public const uint SIGDN_FILESYSPATH = 0x80058000;
    public const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    public const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;
}
