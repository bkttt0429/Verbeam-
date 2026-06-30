$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$py = Join-Path $here "venv\Scripts\python.exe"
$bench = Join-Path $here "bench_mangaocr.py"
$outDir = Join-Path $here "outputs\bench"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$cfgs = @(
  @{ep="cpu"; dev=0; label="CPU"},
  @{ep="dml"; dev=0; label="DML device0 (iGPU Iris Xe)"},
  @{ep="dml"; dev=1; label="DML device1 (dGPU 3050Ti)"}
)
foreach($c in $cfgs){
  Write-Host "`n========== $($c.label) =========="
  $base = [int]((nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits) -split "`n")[0]
  $outf = Join-Path $outDir "out_$($c.ep)$($c.dev).txt"
  $p = Start-Process -FilePath $py -ArgumentList $bench,"--ep",$c.ep,"--device",$c.dev,"--hold","5" -PassThru -RedirectStandardOutput $outf -RedirectStandardError "$outf.err" -WindowStyle Hidden
  $peakGpu = $base
  while(-not $p.HasExited){
    $u = [int]((nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits) -split "`n")[0]
    if($u -gt $peakGpu){ $peakGpu = $u }
    Start-Sleep -Milliseconds 400
  }
  $u = [int]((nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits) -split "`n")[0]
  if($u -gt $peakGpu){ $peakGpu = $u }
  $line = (Get-Content $outf | Where-Object { $_ -like "BENCHJSON*" })
  Write-Host ("dGPU used baseline={0}MiB  peak={1}MiB  delta={2}MiB" -f $base,$peakGpu,($peakGpu-$base))
  if($line){ Write-Host ($line -replace "^BENCHJSON","") } else { Write-Host "NO JSON; err:"; Get-Content "$outf.err" -Tail 6 -EA SilentlyContinue }
}
