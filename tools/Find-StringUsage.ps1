# Finds ldstr constants in Assembly-CSharp.dll matching a regex, with the containing method.
# Uses Mono.Cecil (shipped with BepInEx), so no assembly resolution is needed.
param(
    [string]$NuclearOptionRoot = $env:NUCLEAR_OPTION_ROOT,
    [string]$Assembly,
    [string]$Cecil,
    [Parameter(Mandatory = $true)][string]$Pattern
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
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($ins in $m.Body.Instructions) {
            if ($ins.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr) {
                $s = [string]$ins.Operand
                if ($re.IsMatch($s)) {
                    Write-Output ("{0}::{1}  +{2}  `"{3}`"" -f $t.FullName, $m.Name, $ins.Offset, $s)
                }
            }
        }
    }
    foreach ($n in $t.NestedTypes) { Walk $n }
}

foreach ($t in $asm.MainModule.Types) { Walk $t }
