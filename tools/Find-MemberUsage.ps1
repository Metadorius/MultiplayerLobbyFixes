# Lists every method that references a field or calls a method whose name matches a regex, together with
# the type that declares the member. Answers "who reads this" for assemblies whose names are the only
# handle, which is how the obfuscated parts of Rewired have to be navigated.
#
# Example: find the code that applies a setting by its accessor or backing field name.
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

function Walk([Mono.Cecil.TypeDefinition]$t) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($ins in $m.Body.Instructions) {
            $op = $ins.Operand
            if ($op -is [Mono.Cecil.FieldReference] -and $op.Name -match $Pattern) {
                Write-Output ("FIELD  {0}::{1}  ->  {2}.{3}" -f $t.FullName, $m.Name, $op.DeclaringType.Name, $op.Name)
            }
            elseif ($op -is [Mono.Cecil.MethodReference] -and $op.Name -match $Pattern) {
                Write-Output ("CALL   {0}::{1}  ->  {2}.{3}" -f $t.FullName, $m.Name, $op.DeclaringType.Name, $op.Name)
            }
        }
    }
    foreach ($n in $t.NestedTypes) { Walk $n }
}

foreach ($t in $asm.MainModule.Types) { Walk $t }
