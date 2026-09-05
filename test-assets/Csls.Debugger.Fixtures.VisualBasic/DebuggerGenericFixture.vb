Imports System.Diagnostics
Imports System.Threading.Tasks

Namespace Global.Csls.Debugger.Fixtures.VisualBasic
    ''' <summary>
    ''' Provides a closed generic Visual Basic value for debugger construction tests.
    ''' </summary>
    <DebuggerDisplay("generic={Me._value}", Type:="visual-basic-generic")>
    Friend NotInheritable Class DebuggerGenericFixture(Of T)
        Private ReadOnly _value As T

        ''' <summary>
        ''' Initializes the generic Visual Basic debugger value with its default value.
        ''' </summary>
        Friend Sub New()
            _value = Nothing
        End Sub

        ''' <summary>
        ''' Initializes the generic Visual Basic debugger value.
        ''' </summary>
        ''' <param name="value">The value retained by the constructed instance.</param>
        Friend Sub New(value As T)
            _value = value
        End Sub

        ''' <summary>
        ''' Gets the value retained by the constructed instance.
        ''' </summary>
        Friend ReadOnly Property Value As T
            Get
                Return _value
            End Get
        End Property

        ''' <summary>
        ''' Consumes captured generic parameters after asynchronous suspension.
        ''' </summary>
        ''' <param name="argument">The value inspected and replaced by the debugger.</param>
        ''' <param name="replacement">The expected value after debugger assignment.</param>
        ''' <param name="unused">An unused source parameter for optimized-storage inspection.</param>
        ''' <returns>Zero when execution consumes the assigned parameter.</returns>
        Friend Async Function RunCapturedAsync(argument As T, replacement As T, unused As Integer) As Task(Of Integer)
            Await Task.Yield()
            Console.Write(argument)
            GC.KeepAlive(_value)
            GC.KeepAlive(replacement)
            Return If(EqualityComparer(Of T).Default.Equals(argument, replacement), 0, 1)
        End Function
    End Class
End Namespace
