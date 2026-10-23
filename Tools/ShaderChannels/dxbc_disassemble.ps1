# Disassembles every DXBC blob inside FASTBIN0 shader cache files (fxc/*.hlsl_compiled) with d3dcompiler_47.
# Usage: powershell -File dxbc_disassemble.ps1 -InDir <dir> -OutDir <dir> [-List <file with one file name per line>]
# Output: <OutDir>\<file name>.<blob index>.asm
param([string]$InDir, [string]$OutDir, [string]$List = "")
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class D3D {
  [DllImport("d3dcompiler_47.dll")] public static extern int D3DDisassemble(byte[] src, UIntPtr size, uint flags, string comments, out IntPtr blob);
  [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface ID3DBlob { [PreserveSig] IntPtr GetBufferPointer(); [PreserveSig] UIntPtr GetBufferSize(); }
  public static string Dis(byte[] b) {
    IntPtr p; int hr = D3DDisassemble(b, (UIntPtr)b.Length, 0, null, out p);
    if (hr != 0) return "HRESULT " + hr.ToString("X");
    var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(p);
    return Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize());
  }
}
"@
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if ($List -ne "") { $names = [IO.File]::ReadAllLines($List) } else { $names = Get-ChildItem $InDir -Filter *.hlsl_compiled | ForEach-Object { $_.Name } }
foreach ($name in $names) {
  $name = [IO.Path]::GetFileName($name)
  if ($name -eq "") { continue }
  $d = [IO.File]::ReadAllBytes((Join-Path $InDir $name)); $n = 0
  for ($i = 0; $i -le $d.Length - 32; $i++) {
    if ($d[$i] -eq 0x44 -and $d[$i+1] -eq 0x58 -and $d[$i+2] -eq 0x42 -and $d[$i+3] -eq 0x43) {
      $size = [BitConverter]::ToUInt32($d, $i + 24)
      if ($size -gt 0 -and $i + $size -le $d.Length) {
        $b = New-Object byte[] $size; [Array]::Copy($d, $i, $b, 0, $size)
        [IO.File]::WriteAllText((Join-Path $OutDir ("{0}.{1}.asm" -f $name, $n)), [D3D]::Dis($b)); $n++; $i += $size - 1
      }
    }
  }
}
"done"
