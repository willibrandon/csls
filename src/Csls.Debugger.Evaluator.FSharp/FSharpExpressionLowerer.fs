namespace Csls.Debugger.Evaluator.FSharp

open System
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open Csls.Debugger.Contracts
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// <summary>
/// Lowers compiler-parsed F# expressions to the debugger expression IR.
/// </summary>
[<RequireQualifiedAccess>]
module FSharpExpressionLowerer =
    let private checker = FSharpChecker.Create()
    let private noText: string = Unchecked.defaultof<string>

    let private node kind text children =
        DebugExpressionNode(
            kind,
            DebugExpressionOperator.None,
            text,
            noText,
            children |> List.toArray)

    let private operatorNode kind operation children =
        DebugExpressionNode(kind, operation, noText, noText, children |> List.toArray)

    let private literal text typeName =
        DebugExpressionNode(
            DebugExpressionNodeKind.Literal,
            DebugExpressionOperator.None,
            text,
            typeName,
            Array.Empty<DebugExpressionNode>())

    let private unsupported description =
        NotSupportedException(
            $"F# expression {description} is not supported by safe evaluation.")

    let private lowerConstant constant =
        match constant with
        | SynConst.Bool value -> literal (if value then "true" else "false") "bool"
        | SynConst.SByte value -> literal (value.ToString(CultureInfo.InvariantCulture)) "sbyte"
        | SynConst.Byte value -> literal (value.ToString(CultureInfo.InvariantCulture)) "byte"
        | SynConst.Int16 value -> literal (value.ToString(CultureInfo.InvariantCulture)) "short"
        | SynConst.UInt16 value -> literal (value.ToString(CultureInfo.InvariantCulture)) "ushort"
        | SynConst.Int32 value -> literal (value.ToString(CultureInfo.InvariantCulture)) "int"
        | SynConst.UInt32 value -> literal (value.ToString(CultureInfo.InvariantCulture)) "uint"
        | SynConst.Int64 value -> literal (value.ToString(CultureInfo.InvariantCulture)) "long"
        | SynConst.UInt64 value -> literal (value.ToString(CultureInfo.InvariantCulture)) "ulong"
        | SynConst.Single value -> literal (value.ToString("R", CultureInfo.InvariantCulture)) "float"
        | SynConst.Double value -> literal (value.ToString("R", CultureInfo.InvariantCulture)) "double"
        | SynConst.Char value -> literal (value.ToString()) "char"
        | SynConst.Decimal value -> literal (value.ToString(CultureInfo.InvariantCulture)) "decimal"
        | SynConst.String (value, _, _) -> literal value "string"
        | other -> raise (unsupported $"literal '{other}'")

    let private operatorName expression =
        match expression with
        | SynExpr.Ident identifier -> identifier.idText
        | SynExpr.LongIdent(longDotId = identifier) ->
            match identifier.LongIdent with
            | [ name ] -> name.idText
            | _ -> raise (unsupported "operator qualification")
        | _ -> raise (unsupported "application")

    let private unaryOperator name =
        match name with
        | "op_UnaryPlus" -> DebugExpressionOperator.UnaryPlus
        | "op_UnaryNegation" -> DebugExpressionOperator.Negate
        | "not" -> DebugExpressionOperator.LogicalNot
        | "op_LogicalNot" -> DebugExpressionOperator.OnesComplement
        | _ -> raise (unsupported $"operator '{name}'")

    let private binaryOperator name =
        match name with
        | "op_Addition" -> DebugExpressionOperator.Add
        | "op_Subtraction" -> DebugExpressionOperator.Subtract
        | "op_Multiply" -> DebugExpressionOperator.Multiply
        | "op_Division" -> DebugExpressionOperator.Divide
        | "op_Modulus" -> DebugExpressionOperator.Remainder
        | "op_Equality" -> DebugExpressionOperator.Equal
        | "op_Inequality" -> DebugExpressionOperator.NotEqual
        | "op_LessThan" -> DebugExpressionOperator.LessThan
        | "op_LessThanOrEqual" -> DebugExpressionOperator.LessThanOrEqual
        | "op_GreaterThan" -> DebugExpressionOperator.GreaterThan
        | "op_GreaterThanOrEqual" -> DebugExpressionOperator.GreaterThanOrEqual
        | "op_BooleanAnd" -> DebugExpressionOperator.LogicalAnd
        | "op_BooleanOr" -> DebugExpressionOperator.LogicalOr
        | "op_BitwiseAnd" -> DebugExpressionOperator.BitwiseAnd
        | "op_BitwiseOr" -> DebugExpressionOperator.BitwiseOr
        | "op_ExclusiveOr" -> DebugExpressionOperator.ExclusiveOr
        | _ -> raise (unsupported $"operator '{name}'")

    let private conversionType name =
        match name with
        | "sbyte" -> Some "sbyte"
        | "byte" -> Some "byte"
        | "int16" -> Some "short"
        | "uint16" -> Some "ushort"
        | "int" -> Some "int"
        | "uint32" -> Some "uint"
        | "int64" -> Some "long"
        | "uint64" -> Some "ulong"
        | "nativeint" -> Some "nint"
        | "unativeint" -> Some "nuint"
        | "char" -> Some "char"
        | "float32" -> Some "float"
        | "float" -> Some "double"
        | "decimal" -> Some "decimal"
        | _ -> None

    let private conversionNode typeName operand =
        DebugExpressionNode(
            DebugExpressionNodeKind.Conversion,
            DebugExpressionOperator.None,
            noText,
            typeName,
            [| operand |])

    let rec private lowerLongIdentifier (names: Ident list) =
        match names with
        | [] -> raise (unsupported "empty identifier")
        | first :: remaining ->
            remaining
            |> List.fold
                (fun (receiver: DebugExpressionNode) (name: Ident) ->
                    node DebugExpressionNodeKind.MemberAccess name.idText [ receiver ])
                (if first.idText = "null" then literal noText noText
                 else node DebugExpressionNodeKind.Identifier first.idText [])

    let rec private lower (expression: SynExpr) =
        match expression with
        | SynExpr.Paren(expr = inner) -> lower inner
        | SynExpr.Ident identifier -> lowerLongIdentifier [ identifier ]
        | SynExpr.LongIdent(longDotId = identifier) ->
            lowerLongIdentifier identifier.LongIdent
        | SynExpr.Const(constant = constant) -> lowerConstant constant
        | SynExpr.DotGet(expr = receiver; longDotId = members) ->
            members.LongIdent
            |> List.fold
                (fun current memberName ->
                    node DebugExpressionNodeKind.MemberAccess memberName.idText [ current ])
                (lower receiver)
        | SynExpr.DotIndexedGet(objectExpr = receiver; indexArgs = indexes) ->
            let loweredIndexes =
                match indexes with
                | SynExpr.Tuple(exprs = expressions) -> expressions |> List.map lower
                | single -> [ lower single ]

            node DebugExpressionNodeKind.ElementAccess noText (lower receiver :: loweredIndexes)
        | SynExpr.App(funcExpr = SynExpr.DotGet(expr = receiver; longDotId = members);
                      argExpr = argument) ->
            let names = members.LongIdent
            match List.rev names with
            | [] -> raise (unsupported "empty method name")
            | methodName :: reversedReceiverMembers ->
                let loweredReceiver =
                    reversedReceiverMembers
                    |> List.rev
                    |> List.fold
                        (fun current memberName ->
                            node
                                DebugExpressionNodeKind.MemberAccess
                                memberName.idText
                                [ current ])
                        (lower receiver)
                let arguments =
                    match argument with
                    | SynExpr.Const(constant = SynConst.Unit) -> []
                    | SynExpr.Tuple(exprs = expressions) -> expressions |> List.map lower
                    | single -> [ lower single ]

                node
                    DebugExpressionNodeKind.Invocation
                    methodName.idText
                    (loweredReceiver :: arguments)
        | SynExpr.App(funcExpr = SynExpr.LongIdent(longDotId = members);
                      argExpr = argument) when members.LongIdent.Length >= 2 ->
            match List.rev members.LongIdent with
            | methodName :: reversedReceiverNames ->
                let loweredReceiver =
                    reversedReceiverNames
                    |> List.rev
                    |> lowerLongIdentifier
                let arguments =
                    match argument with
                    | SynExpr.Const(constant = SynConst.Unit) -> []
                    | SynExpr.Tuple(exprs = expressions) -> expressions |> List.map lower
                    | single -> [ lower single ]

                node
                    DebugExpressionNodeKind.Invocation
                    methodName.idText
                    (loweredReceiver :: arguments)
            | _ -> raise (unsupported "empty method name")
        | SynExpr.App(funcExpr = SynExpr.App(funcExpr = operation; argExpr = left);
                      argExpr = right) ->
            operatorNode
                DebugExpressionNodeKind.Binary
                (binaryOperator (operatorName operation))
                [ lower left; lower right ]
        | SynExpr.App(funcExpr = operation; argExpr = operand) ->
            match conversionType (operatorName operation) with
            | Some typeName -> conversionNode typeName (lower operand)
            | None ->
                operatorNode
                    DebugExpressionNodeKind.Unary
                    (unaryOperator (operatorName operation))
                    [ lower operand ]
        | SynExpr.IfThenElse(ifExpr = condition;
                             thenExpr = whenTrue;
                             elseExpr = Some whenFalse) ->
            node
                DebugExpressionNodeKind.Conditional
                noText
                [ lower condition; lower whenTrue; lower whenFalse ]
        | other -> raise (unsupported $"kind '{other.GetType().Name}'")

    let private findExpression parseTree =
        match parseTree with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            modules
            |> List.tryPick (fun (SynModuleOrNamespace(decls = declarations)) ->
                declarations
                |> List.tryPick (fun declaration ->
                    match declaration with
                    | SynModuleDecl.Let(bindings = SynBinding(expr = expression) :: _) ->
                        Some expression
                    | _ -> None))
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("F# parsing produced no expression binding.")))
        | _ -> raise (InvalidDataException("F# parsing produced no implementation tree."))

    let private bind
        (expression: string)
        (cancellationToken: CancellationToken)
        : Async<DebugExpressionPlan> =
        async {
            if String.IsNullOrWhiteSpace(expression) then
                return
                    raise (
                        ArgumentException(
                            "An F# debugger expression is required.",
                            nameof expression))

            cancellationToken.ThrowIfCancellationRequested()
            let source = $"module CslsExpression\nlet result = ({expression})"
            let fileName = "CslsExpression.fs"
            let options =
                { FSharpParsingOptions.Default with
                    SourceFiles = [| fileName |] }
            let! parsed =
                checker.ParseFile(fileName, SourceText.ofString source, options, cache = false)
            cancellationToken.ThrowIfCancellationRequested()
            match
                parsed.Diagnostics
                |> Array.tryFind (fun diagnostic ->
                    diagnostic.Severity = FSharpDiagnosticSeverity.Error)
            with
            | Some diagnostic ->
                return raise (ArgumentException(diagnostic.Message, nameof expression))
            | None ->
                return
                    DebugExpressionPlan(
                        DebuggerEvaluatorProtocol.CurrentPlanVersion,
                        DebugExpressionLanguage.FSharp,
                        parsed.ParseTree |> findExpression |> lower)
        }

    /// <summary>
    /// Parses and lowers one F# expression with the official compiler service.
    /// </summary>
    /// <param name="expression">The F# source expression.</param>
    /// <param name="cancellationToken">Cancels compiler parsing.</param>
    /// <returns>The validated language-neutral expression plan.</returns>
    [<CompiledName("BindAsync")>]
    let bindAsync (expression: string) (cancellationToken: CancellationToken) : Task<DebugExpressionPlan> =
        Async.StartAsTask(
            bind expression cancellationToken,
            cancellationToken = cancellationToken)
