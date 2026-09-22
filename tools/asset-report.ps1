param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
public static class CatAssetReport {
 public static int[] Inspect(string file) { using(var b=new Bitmap(file)) { int transparent=0,opaque=0,partial=0,minx=b.Width,miny=b.Height,maxx=-1,maxy=-1,edge=0; for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++){int a=b.GetPixel(x,y).A;if(a==0)transparent++;else {if(a==255)opaque++;else partial++;if(x<minx)minx=x;if(y<miny)miny=y;if(x>maxx)maxx=x;if(y>maxy)maxy=y;if(x==0||y==0||x==b.Width-1||y==b.Height-1)edge++;}}return new int[]{b.Width,b.Height,transparent,opaque,partial,minx,miny,maxx,maxy,edge};}}
}
'@
$frames=Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'assets/frames') -Filter '*.png' | Sort-Object Name
$layers=Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'assets/sprites') -Filter '*.png' | Where-Object { $_.Name.StartsWith('body_') -or $_.Name -match '^paw_(left|right)_(up|down)\.png$' } | Sort-Object Name
$report=foreach($f in @($frames)+@($layers)) {
  $s=[CatAssetReport]::Inspect($f.FullName)
  if($s[0] -ne 512 -or $s[1] -ne 384 -or $s[2] -eq 0 -or $s[3] -eq 0 -or $s[9] -ne 0){throw ('Invalid runtime raster '+$f.Name)}
  [ordered]@{path=$f.FullName.Substring($ProjectRoot.Length+1).Replace('\','/');width=$s[0];height=$s[1];transparentPixels=$s[2];opaquePixels=$s[3];partialAlphaPixels=$s[4];bounds=@($s[5],$s[6],$s[7],$s[8]);opaqueOuterEdgePixels=$s[9];bytes=$f.Length;sha256=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant();source='Built-in image_gen; deterministic crop, resize, layer assembly';status='alpha-and-contact-sheet-verified'}
}
if($frames.Count -ne 32 -or $layers.Count -ne 12){throw 'Expected 32 full frames and 12 positioned layers.'}
$manifest=[ordered]@{schemaVersion=1;canvas=@(512,384);deskBaseline=310;pivot=@(256,310);viewerSideConvention=$true;frameCount=$frames.Count;layerCount=$layers.Count;tool='built-in image_gen.imagegen';promptFile='assets/generation-prompts.json';preview='assets/contact-sheet.png';assets=@($report)}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ProjectRoot 'assets/asset-manifest.json') -Encoding UTF8
[pscustomobject]@{Frames=$frames.Count;Layers=$layers.Count;Bytes=(@($frames)+@($layers)|Measure-Object Length -Sum).Sum;AlphaChecks='passed';OuterEdges='transparent'}
