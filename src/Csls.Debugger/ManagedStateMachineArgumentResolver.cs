using Csls.Debugger.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Maps symbol-identified C# and Visual Basic state machines to their source parameters.
/// </summary>
internal static class ManagedStateMachineArgumentResolver
{
    /// <summary>
    /// Resolves source parameter storage for the current physical state-machine frame.
    /// </summary>
    /// <param name="frame">The stopped frame and its exact symbol generation.</param>
    /// <returns>Ordered source parameters, or null for an ordinary runtime frame.</returns>
    internal static IReadOnlyList<ManagedStateMachineVariable>? Resolve(ManagedFrameHandle frame)
    {
        if (frame.ExpressionLanguage is not (DebugExpressionLanguage.CSharp or DebugExpressionLanguage.VisualBasic))
        {
            return null;
        }

        using DebugSymbolReader? symbols = frame.OpenSymbols();
        if (symbols?.GetStateMachineKickoffMethod(frame.MethodToken) is not uint kickoffToken)
        {
            return null;
        }

        using PEReader? pe = frame.OpenPeReader();
        if (pe is null)
        {
            return null;
        }

        using var metadata = new ManagedMetadataImage(pe.GetMetadataReader(), frame.MetadataDeltas);
        var kickoff = (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)kickoffToken));
        MethodDefinition method = metadata.GetMethodDefinition(kickoff);
        bool visualBasic = frame.ExpressionLanguage == DebugExpressionLanguage.VisualBasic;
        var arguments = new List<ManagedStateMachineVariable>();
        if ((method.Attributes & MethodAttributes.Static) == 0)
        {
            arguments.Add(new ManagedStateMachineVariable(visualBasic ? "Me" : "this",
                visualBasic ? "$VB$Me" : "<>4__this", kickoffToken, null, null));
        }

        foreach (ParameterHandle handle in metadata.GetParameters(kickoff))
        {
            Parameter parameter = metadata.GetParameter(handle);
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            string name = metadata.GetString(parameter.Name);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            arguments.Add(new ManagedStateMachineVariable(name,
                visualBasic ? "$VB$Local_" + name : name,
                kickoffToken, parameter.SequenceNumber - 1,
                ManagedTupleElementNameReader.ReadAttribute(metadata, handle)));
        }

        return arguments;
    }
}
