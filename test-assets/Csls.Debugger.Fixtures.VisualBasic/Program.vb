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
        answer += 1
        Console.Write(arguments(2))
        Console.Out.Flush()
        While Not File.Exists(arguments(0))
            Thread.SpinWait(10_000)
        End While

        GC.KeepAlive(value)
        Return answer - 42
    End Function
End Module
