namespace Murchalka.ClientRealtime.Runtime;

internal sealed class ModuleDependencyException : Exception
{
    public ModuleDependencyException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

