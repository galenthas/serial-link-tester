using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SerialRf {
    static class Theme {
        public static readonly Color Bg = Color.FromArgb(13,18,26), Surface = Color.FromArgb(21,29,40), Field = Color.FromArgb(29,40,54), Border = Color.FromArgb(47,63,80);
        public static readonly Color Navy = Color.FromArgb(233,240,247), Muted = Color.FromArgb(158,177,195), Teal = Color.FromArgb(67,216,190), Red = Color.FromArgb(255,126,126), Amber = Color.FromArgb(246,198,107);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void WindowChrome(Form form) {
            try {
                int enabled=1; DwmSetWindowAttribute(form.Handle,20,ref enabled,4);
                int caption=ColorTranslator.ToWin32(Bg), text=ColorTranslator.ToWin32(Navy), border=ColorTranslator.ToWin32(Border);
                DwmSetWindowAttribute(form.Handle,35,ref caption,4);DwmSetWindowAttribute(form.Handle,36,ref text,4);DwmSetWindowAttribute(form.Handle,34,ref border,4);
            } catch(DllNotFoundException) { } catch(EntryPointNotFoundException) { }
        }
        public static void Notice(IWin32Window owner,string message,string title) {
            using(Form f=new Form { Text=title,BackColor=Bg,ForeColor=Navy,Font=new Font("Segoe UI",10),ClientSize=new Size(560,240),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false,FormBorderStyle=FormBorderStyle.FixedDialog,Padding=new Padding(24) }) {
                RichTextBox content=new RichTextBox { Text=message,Dock=DockStyle.Fill,BackColor=Bg,ForeColor=Navy,ReadOnly=true,BorderStyle=BorderStyle.None,Font=f.Font,TabStop=false };
                Panel footer=new Panel { Dock=DockStyle.Bottom,Height=44 };
                DarkButton ok=new DarkButton { Text="OK",Primary=true,Dock=DockStyle.Right,Width=96,DialogResult=DialogResult.OK };
                footer.Controls.Add(ok);f.Controls.Add(content);f.Controls.Add(footer);f.AcceptButton=ok;f.CancelButton=ok;f.HandleCreated+=delegate { WindowChrome(f); };f.ShowDialog(owner);
            }
        }
    }
    sealed class DarkButton : Button {
        bool hover;
        public bool Primary, Selected;
        public DarkButton() { FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;BackColor=Theme.Field;ForeColor=Theme.Navy;Cursor=Cursors.Hand;DoubleBuffered=true; }
        protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate();base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e) {
            Color bg=!Enabled?Theme.Surface:Primary?Theme.Teal:Selected?Color.FromArgb(28,66,65):hover?Color.FromArgb(42,57,74):Theme.Field;
            Color fg=!Enabled?Color.FromArgb(103,123,143):Primary?Theme.Bg:Selected?Theme.Teal:Theme.Navy;
            e.Graphics.Clear(bg);
            using(Pen p=new Pen(Focused?Theme.Teal:Selected?Theme.Teal:Theme.Border))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,fg,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        }
    }
    sealed class DarkCombo : ComboBox {
        public DarkCombo() { BackColor=Theme.Field;ForeColor=Theme.Navy;DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=22;FlatStyle=FlatStyle.Flat; }
        protected override void OnDrawItem(DrawItemEventArgs e) {
            if(e.Index<0)return;
            bool selected=(e.State&DrawItemState.Selected)!=0;
            using(Brush b=new SolidBrush(selected?Color.FromArgb(36,72,78):Theme.Field))e.Graphics.FillRectangle(b,e.Bounds);
            TextRenderer.DrawText(e.Graphics,GetItemText(Items[e.Index]),Font,new Rectangle(e.Bounds.X+7,e.Bounds.Y,e.Bounds.Width-10,e.Bounds.Height),Enabled?Theme.Navy:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        }
    }
    sealed class DarkNumber : UserControl {
        readonly TextBox input=new TextBox();
        readonly int minimum,maximum; int last;
        public int Value {
            get { int value;if(!int.TryParse(input.Text,out value)||value<minimum||value>maximum)throw new ArgumentException((AccessibleName??"Value")+" must be between "+minimum+" and "+maximum+".");return value; }
            set { last=Math.Max(minimum,Math.Min(maximum,value));input.Text=last.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        }
        public DarkNumber(int min,int max,int value) {
            minimum=min;maximum=max;BackColor=Theme.Field;Padding=new Padding(6,4,2,3);Height=28;
            DarkButton minus=new DarkButton { Text="−",Dock=DockStyle.Right,Width=22,TabStop=false },plus=new DarkButton { Text="+",Dock=DockStyle.Right,Width=22,TabStop=false };
            input.BackColor=Theme.Field;input.ForeColor=Theme.Navy;input.BorderStyle=BorderStyle.None;input.Dock=DockStyle.Fill;input.Font=new Font("Segoe UI",9.5f);
            input.KeyPress+=delegate(object sender,KeyPressEventArgs e) { if(!char.IsControl(e.KeyChar)&&!char.IsDigit(e.KeyChar))e.Handled=true; };
            minus.Click+=delegate { int n;Value=(int.TryParse(input.Text,out n)?n:last)-1; };plus.Click+=delegate { int n;Value=(int.TryParse(input.Text,out n)?n:last)+1; };
            input.KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down) { int n;Value=(int.TryParse(input.Text,out n)?n:last)+(e.KeyCode==Keys.Up?1:-1);e.Handled=true; } };
            Controls.Add(input);Controls.Add(minus);Controls.Add(plus);Value=value;
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e);using(Pen p=new Pen(Theme.Border))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1); }
    }
    sealed class DarkProgress : Control {
        int value;
        public int Value { get{return value;}set{this.value=Math.Max(0,Math.Min(100,value));Invalidate();} }
        public DarkProgress() { DoubleBuffered=true; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e);int y=Height/2-3;using(Brush b=new SolidBrush(Theme.Field))e.Graphics.FillRectangle(b,0,y,Width,6);using(Brush b=new SolidBrush(Theme.Teal))e.Graphics.FillRectangle(b,0,y,Width*value/100f,6); }
    }
}
