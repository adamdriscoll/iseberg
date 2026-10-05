using System.Management.Automation;

namespace Iseberg.Core;

public enum BreakpointKind { Line, Command, Variable }

public sealed record BreakpointSpec(BreakpointKind Kind, string? ScriptPath = null, string Target = "",
    int Line = 0, string Condition = "", bool Enabled = true, VariableAccessMode AccessMode = VariableAccessMode.Write,
    string Action = "")
{
    public void Validate()
    {
        if (Kind == BreakpointKind.Line && (string.IsNullOrWhiteSpace(ScriptPath) || Line < 1))
            throw new ArgumentException("A line breakpoint requires a saved script and a positive line number.");
        if (Kind != BreakpointKind.Line && string.IsNullOrWhiteSpace(Target))
            throw new ArgumentException("Enter a command or variable name.");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(AccessMode))
            throw new ArgumentException("Invalid breakpoint kind or variable access mode.");
        if (!string.IsNullOrWhiteSpace(Condition) && !string.IsNullOrWhiteSpace(Action))
            throw new ArgumentException("Use a condition or an action, not both.");
        if (!string.IsNullOrWhiteSpace(Condition)) _ = ScriptBlock.Create($"if ({Condition}) {{ break }}");
        if (!string.IsNullOrWhiteSpace(Action)) _ = ScriptBlock.Create(Action);
    }
}

public sealed record DebugBreakpoint(int Id, BreakpointSpec Spec, int HitCount)
{
    public override string ToString() =>
        $"#{Id} {(Spec.Enabled ? "[on]" : "[off]")} {Spec.Kind}: " +
        (Spec.Kind == BreakpointKind.Line ? $"{Spec.ScriptPath}:{Spec.Line}" : Spec.Target) +
        (Spec.Kind == BreakpointKind.Variable ? $" ({Spec.AccessMode})" : "") +
        (string.IsNullOrWhiteSpace(Spec.Condition) ? "" : $" if {Spec.Condition}") +
        (string.IsNullOrWhiteSpace(Spec.Action) ? "" : $" {{{Spec.Action}}}");
}

public sealed record DebugValue(string Name, string Value, string Type, string? Error = null)
{
    public override string ToString() => Error is null ? $"{Name} = {Value}  [{Type}]" : $"{Name}: {Error}";
}

public sealed record DebugFrame(string FunctionName, string? ScriptPath, int Line)
{
    public override string ToString() => $"{FunctionName} - {ScriptPath ?? "<interactive>"}:{Line}";
}

public sealed record DebugSnapshot(IReadOnlyList<DebugValue> Variables, IReadOnlyList<DebugValue> Watches,
    IReadOnlyList<DebugFrame> CallStack);
