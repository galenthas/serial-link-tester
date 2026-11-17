using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconArtwork {
    public static void Main(string[] args) { Build(args[0]); }
    // Native vector artwork mirrors assets/SerialLinkTester.svg.
    static Bitmap Render(int size) {
        using(Bitmap canvas=new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb)) {
            using(Graphics g=Graphics.FromImage(canvas)) {
                g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;
                g.ScaleTransform(size*4/256f,size*4/256f);
                using(GraphicsPath path=new GraphicsPath()) {
                    path.AddArc(8,8,112,112,180,90);path.AddArc(136,8,112,112,270,90);
                    path.AddArc(136,136,112,112,0,90);path.AddArc(8,136,112,112,90,90);path.CloseFigure();
                    using(Brush fill=new SolidBrush(Color.FromArgb(13,18,26)))g.FillPath(fill,path);
                    using(Pen edge=new Pen(Color.FromArgb(47,63,80),8))g.DrawPath(edge,path);
                }
                using(Pen teal=new Pen(Color.FromArgb(67,216,190),16))
                using(Pen white=new Pen(Color.FromArgb(233,240,247),16)) {
                    foreach(Pen pen in new[]{teal,white}) { pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round; }
                    g.DrawLine(teal,56,86,182,86);g.DrawLines(teal,new[]{new PointF(158,62),new PointF(182,86),new PointF(158,110)});
                    g.DrawLine(white,200,170,74,170);g.DrawLines(white,new[]{new PointF(98,146),new PointF(74,170),new PointF(98,194)});
                }
            }
            Bitmap result=new Bitmap(size,size,PixelFormat.Format32bppArgb);
            using(Graphics g=Graphics.FromImage(result)) { g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(canvas,new Rectangle(0,0,size,size),0,0,canvas.Width,canvas.Height,GraphicsUnit.Pixel); }
            return result;
        }
    }
    public static void Build(string directory) {
        Directory.CreateDirectory(directory);
        int[] sizes={16,24,32,48,64,128,256};byte[][] pngs=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++)using(Bitmap bitmap=Render(sizes[i]))using(MemoryStream stream=new MemoryStream()) {
            bitmap.Save(stream,ImageFormat.Png);pngs[i]=stream.ToArray();
            if(sizes[i]==256)bitmap.Save(Path.Combine(directory,"SerialLinkTester.png"),ImageFormat.Png);
        }
        using(BinaryWriter writer=new BinaryWriter(File.Create(Path.Combine(directory,"SerialLinkTester.ico")))) {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);
            int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) {
                writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));
                writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);
                writer.Write(pngs[i].Length);writer.Write(offset);offset+=pngs[i].Length;
            }
            foreach(byte[] png in pngs)writer.Write(png);
        }
    }
}
