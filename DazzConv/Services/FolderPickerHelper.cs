using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DazzConv.Services
{
    public static class FolderPickerHelper
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO bi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder path);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr ptr);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public string pszDisplayName;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        private const uint BIF_RETURNONLYFSDIRS = 0x0001;
        private const uint BIF_NEWDIALOGSTYLE = 0x0040;
        private const uint BIF_EDITBOX = 0x0010;
        private const uint BIF_USENEWUI = BIF_EDITBOX | BIF_NEWDIALOGSTYLE;

        public static async Task<string?> PickFolderAsync(string title = "Выберите папку")
        {
            return await Task.Run(() =>
            {
                string? result = null;

                // Win32 API требует STA поток
                var thread = new System.Threading.Thread(() =>
                {
                    var bi = new BROWSEINFO
                    {
                        lpszTitle = title,
                        ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI
                    };

                    IntPtr pidl = SHBrowseForFolder(ref bi);

                    if (pidl != IntPtr.Zero)
                    {
                        try
                        {
                            var sb = new StringBuilder(260);
                            if (SHGetPathFromIDList(pidl, sb))
                            {
                                result = sb.ToString();
                            }
                        }
                        finally
                        {
                            CoTaskMemFree(pidl);
                        }
                    }
                });

                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                thread.Join();

                return result;
            });
        }
    }
}