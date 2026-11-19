using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SerialRf {
    sealed class ReportTextBox : RichTextBox {
        const int GetScrollPos=0x0400+221, SetScrollPos=0x0400+222, SetRedraw=0x000B;
        const uint RedrawInvalidate=0x0001, RedrawErase=0x0004, RedrawAllChildren=0x0080, RedrawFrame=0x0400;
        [DllImport("user32.dll", EntryPoint="SendMessageW")]
        static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,ref Point point);
        [DllImport("user32.dll", EntryPoint="SendMessageW")]
        static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RedrawWindow(IntPtr window,IntPtr rectangle,IntPtr region,uint flags);

        // Use on the UI thread for live snapshots. Clear() still resets a new test.
        public void UpdateReport(string value) {
            if(Text==value)return;
            if(!IsHandleCreated) { Text=value;return; }
            int start=SelectionStart,length=SelectionLength;
            Point scroll=new Point();IntPtr window=Handle;
            SendMessage(window,GetScrollPos,IntPtr.Zero,ref scroll);
            // Re-enabling redraw on a hidden window can make it visible. Leave hidden tabs alone.
            bool redraw=Visible;
            if(redraw)SendMessage(window,SetRedraw,IntPtr.Zero,IntPtr.Zero);
            try {
                Text=value;
                start=Math.Min(start,TextLength);
                Select(start,Math.Min(length,TextLength-start));
                // Restoring selection can scroll to the caret, so restore the viewport last.
                SendMessage(window,SetScrollPos,IntPtr.Zero,ref scroll);
            } finally {
                if(redraw) {
                    SendMessage(window,SetRedraw,new IntPtr(1),IntPtr.Zero);
                    RedrawWindow(window,IntPtr.Zero,IntPtr.Zero,RedrawInvalidate|RedrawErase|RedrawAllChildren|RedrawFrame);
                }
            }
        }
    }
}
