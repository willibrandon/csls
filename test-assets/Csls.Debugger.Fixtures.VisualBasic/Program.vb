Imports System
Imports System.IO
Imports System.Globalization
Imports System.Threading

''' <summary>
''' Provides the Visual Basic debugger integration fixture entry point.
''' </summary>
Friend Module Program
    ''' <summary>
    ''' Waits after executing a stable source statement for debugger inspection.
    ''' </summary>
    ''' <param name="arguments">The single signal-file path.</param>
    ''' <returns>Zero when the expected local value remains live.</returns>
    Friend Function Main(arguments As String()) As Integer
        Dim answer As Integer = Integer.Parse(arguments(1), CultureInfo.InvariantCulture)
        Dim value = New DebuggerFixtureValue(answer)
        Dim genericValue = New DebuggerGenericFixture(Of Integer)(answer)
        Dim nestedGenericValue = New DebuggerGenericFixture(Of List(Of Integer))(New List(Of Integer)())
        Dim arrayGenericValue = New DebuggerGenericFixture(Of Integer())(Array.Empty(Of Integer)())
        Dim nullableGenericValue = New DebuggerGenericFixture(Of Integer?)(answer)
        Dim referenceValue As Object = value
        Dim nullReference As Object = If(arguments.Length > 100, value, Nothing)
        Dim boxedNumber As Object = answer
        Dim typeOracle As Boolean() = {TypeOf referenceValue Is DebuggerFixtureValue,
            TypeOf referenceValue Is String, TypeOf nullReference Is Object, TypeOf boxedNumber Is Integer}
        Dim pairs As ValueTuple(Of Integer, Integer)() = {
            New ValueTuple(Of Integer, Integer)(0, 142), New ValueTuple(Of Integer, Integer)(151, 152)}
        answer += 1
        Console.Write(arguments(2))
        Console.Out.Flush()
        While Not File.Exists(arguments(0))
            Thread.Sleep(1)
        End While

        GC.KeepAlive(value)
        GC.KeepAlive(genericValue)
        GC.KeepAlive(nestedGenericValue)
        GC.KeepAlive(arrayGenericValue)
        GC.KeepAlive(nullableGenericValue)
        GC.KeepAlive(referenceValue)
        GC.KeepAlive(nullReference)
        GC.KeepAlive(boxedNumber)
        GC.KeepAlive(typeOracle)
        GC.KeepAlive(pairs)
        Return answer - 42
    End Function
End Module
