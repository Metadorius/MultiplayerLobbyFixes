# Lists types in Nuclear Option's Assembly-CSharp.dll matching a regex.
# Uses Mono.Cecil (shipped with BepInEx) so no assembly resolution is needed.
param(
    [string]$NuclearOptionRoot = $env:NUCLEAR_OPTION_ROOT,
    [string]$Assembly,
    [string]$Cecil,
    [string]$Pattern = '.',
    [switch]$Methods
)

if (-not $NuclearOptionRoot -and (-not $Assembly -or -not $Cecil)) {
    throw "Set NUCLEAR_OPTION_ROOT, or pass -NuclearOptionRoot (path to the Nuclear Option install)."
}
if (-not $Assembly) { $Assembly = Join-Path $NuclearOptionRoot 'NuclearOption_Data\Managed\Assembly-CSharp.dll' }
if (-not $Cecil) { $Cecil = Join-Path $NuclearOptionRoot 'BepInEx\core\Mono.Cecil.dll' }

Add-Type -Path $Cecil
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly)
$re = [regex]::new($Pattern, 'IgnoreCase')

function Walk([Mono.Cecil.TypeDefinition]$t) {
    if ($re.IsMatch($t.FullName)) {
        $base = if ($t.BaseType) { ' : ' + $t.BaseType.FullName } else { '' }
        Write-Output ("{0}{1}" -f $t.FullName, $base)
        if ($Methods) {
            foreach ($m in $t.Methods | Where-Object { -not $_.IsGetter -and -not $_.IsSetter }) {
                Write-Output ("    .{0}({1})" -f $m.Name, (($m.Parameters | ForEach-Object { $_.ParameterType.Name }) -join ', '))
            }
        }
    }
    foreach ($n in $t.NestedTypes) { Walk $n }
}

foreach ($t in $asm.MainModule.Types | Sort-Object FullName) { Walk $t }
