param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class CatAssetBuild {
  static Bitmap Blank() { return new Bitmap(512,384,PixelFormat.Format32bppArgb); }
  static void Save(Bitmap b,string path) { b.Save(path,ImageFormat.Png); }
  static Bitmap Scaled(string path) { using(var src=new Bitmap(path)) { var dst=Blank(); using(var g=Graphics.FromImage(dst)) { g.CompositingMode=CompositingMode.SourceCopy; g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.DrawImage(src,new Rectangle(0,0,512,384)); } return dst; } }
  static GraphicsPath PawShape(bool left) {
    var p=new GraphicsPath();
    if(left) { p.AddBezier(43,304,42,281,67,262,100,263); p.AddBezier(100,263,129,263,151,278,152,300); p.AddBezier(152,300,154,320,139,329,108,331); p.AddBezier(108,331,92,332,81,328,77,327); p.AddBezier(77,327,52,329,42,321,43,304); }
    else { p.AddBezier(315,303,314,278,344,262,372,263); p.AddBezier(372,263,404,262,427,278,427,302); p.AddBezier(427,302,427,320,413,330,385,331); p.AddBezier(385,331,366,333,340,330,327,325); p.AddBezier(327,325,317,322,314,315,315,303); }
    p.CloseFigure(); return p;
  }
  static Bitmap Down(Bitmap src,bool left) { var dst=Blank(); using(var g=Graphics.FromImage(dst)) using(var p=PawShape(left)) { g.SetClip(p); g.DrawImageUnscaled(src,0,0); } return dst; }
  static Bitmap Up(string path,bool left) { var dst=Blank(); using(var src=new Bitmap(path)) using(var g=Graphics.FromImage(dst)) { if(!left)src.RotateFlip(RotateFlipType.RotateNoneFlipX); g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.DrawImage(src,new Rectangle(left?39:312,181,116,145)); } return dst; }
  static Bitmap Body(Bitmap basis,Bitmap face) { var dst=(Bitmap)basis.Clone(); for(int y=153;y<283;y++) for(int x=89;x<399;x++) { int d1=Math.Min(Math.Min(x-89,398-x),Math.Min(y-153,263-y)); int d2=Math.Min(Math.Min(x-158,310-x),Math.Min(y-250,282-y)); int d=Math.Max(d1,d2); if(d<0)continue; double a=Math.Min(1.0,(d+1)/5.0); Color c=face.GetPixel(x,y),b=dst.GetPixel(x,y); dst.SetPixel(x,y,Color.FromArgb(255,(int)Math.Round(c.R*a+b.R*(1-a)),(int)Math.Round(c.G*a+b.G*(1-a)),(int)Math.Round(c.B*a+b.B*(1-a)))); } return dst; }
  public static void Run(string root) {
    string sprites=Path.Combine(root,"assets","sprites"),source=Path.Combine(root,"assets","source"),frames=Path.Combine(root,"assets","frames"); Directory.CreateDirectory(frames);
    string[] ids={"neutral","angry","love","excited","sad","surprised","sleepy","confused"};
    using(var basis=Scaled(Path.Combine(source,"body-alpha.png"))) using(var neutral=Scaled(Path.Combine(source,"neutral-generated.png"))) using(var ld=Down(neutral,true)) using(var rd=Down(neutral,false)) using(var lu=Up(Path.Combine(sprites,"paw.png"),true)) using(var ru=Up(Path.Combine(sprites,"paw.png"),false)) {
      Save(ld,Path.Combine(sprites,"paw_left_down.png")); Save(rd,Path.Combine(sprites,"paw_right_down.png")); Save(lu,Path.Combine(sprites,"paw_left_up.png")); Save(ru,Path.Combine(sprites,"paw_right_up.png"));
      for(int i=0;i<ids.Length;i++) { string id=ids[i]; string suffix=id=="neutral"?"-generated.png":id=="confused"?"-generated.png":"-alpha.png"; using(var face=Scaled(Path.Combine(source,id+suffix))) using(var body=Body(basis,face)) { Save(body,Path.Combine(sprites,"body_"+id+".png")); string[] states={"idle","left","right","both"}; foreach(string state in states) { using(var frame=(Bitmap)body.Clone()) { using(var g=Graphics.FromImage(frame)) { g.DrawImageUnscaled(state=="left"||state=="both"?lu:ld,0,0); g.DrawImageUnscaled(state=="right"||state=="both"?ru:rd,0,0); } Save(frame,Path.Combine(frames,id+"_"+state+".png")); if(state=="idle") Save(frame,Path.Combine(sprites,id+".png")); } } } }
    }
    using(var sheet=new Bitmap(1024,896,PixelFormat.Format32bppArgb)) using(var g=Graphics.FromImage(sheet)) using(var font=new Font("Segoe UI",12)) using(var ink=new SolidBrush(Color.FromArgb(55,45,49))) { g.Clear(Color.FromArgb(237,231,237)); int index=0; foreach(string id in ids) { using(var img=new Bitmap(Path.Combine(frames,id+"_idle.png"))) { int x=(index%4)*256,y=(index/4)*224; g.DrawImage(img,new Rectangle(x,y,256,192)); g.DrawString(id,font,ink,x+12,y+193); } index++; } string[] states2={"idle","left","right","both"}; foreach(string s in states2) { int x=(index%4)*256,y=(index/4)*224; using(var img=new Bitmap(Path.Combine(frames,"neutral_"+s+".png"))) g.DrawImage(img,new Rectangle(x,y,256,192)); g.DrawString("neutral / "+s,font,ink,x+12,y+193); index++; } foreach(string s in states2) { int x=(index%4)*256,y=(index/4)*224; using(var img=new Bitmap(Path.Combine(frames,"love_"+s+".png"))) g.DrawImage(img,new Rectangle(x,y,256,192)); g.DrawString("love / "+s,font,ink,x+12,y+193); index++; } Save(sheet,Path.Combine(root,"assets","contact-sheet.png")); }
  }
}
'@
[CatAssetBuild]::Run($ProjectRoot)
Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'assets/frames') -Filter '*.png' | Measure-Object | Select-Object Count
